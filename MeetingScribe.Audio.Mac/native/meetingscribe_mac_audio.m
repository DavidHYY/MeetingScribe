// MeetingScribe macOS native audio helper.
//
// Compiled with -fno-objc-arc (see the csproj clang invocation): objects that must
// live inside plain C structs (msc_mic_session / msc_system_session, handed back to
// managed code as opaque pointers) are retained/released manually here rather than
// relying on ARC, which does not manage lifetime for non-Objective-C-typed struct
// fields anyway. Every alloc/retain below has a matching release in the corresponding
// _stop/_free function.
//
// Exposes a narrow, callback-based C API (meetingscribe_mac_audio.h) so the C# side
// never has to implement SCStreamOutput/AVCaptureAudioDataOutputSampleBufferDelegate
// itself.

#import <AVFoundation/AVFoundation.h>
#import <AppKit/AppKit.h>
#import <AudioToolbox/AudioToolbox.h>
#import <CoreAudio/CoreAudio.h>
#import <CoreMedia/CoreMedia.h>
#import <IOKit/pwr_mgt/IOPMLib.h>
#import <ScreenCaptureKit/ScreenCaptureKit.h>
#import <dispatch/dispatch.h>
#import <stdatomic.h>
#import <stdlib.h>
#import <string.h>
#import <unistd.h>

#include "meetingscribe_mac_audio.h"

#pragma mark - Helpers

static char *msc_strdup(NSString *s)
{
    if (s == nil)
    {
        return NULL;
    }
    const char *utf8 = [s UTF8String];
    return utf8 != NULL ? strdup(utf8) : NULL;
}

#pragma mark - Heap-allocated wait box (bounded semaphore wait + async completion handler)

/// Every "bounded dispatch_semaphore_wait around an async completion handler" in this
/// file used to keep its semaphore and result variables as plain stack locals
/// (optionally __block-qualified). That is only actually safe if the framework we hand
/// the block to copies it internally (a documented requirement of async
/// completion-handler APIs, and true for AVFoundation/ScreenCaptureKit) - relying on
/// that from this file gives no compile-time or runtime guarantee, and the failure
/// mode if it were ever violated is silent stack corruption, not a crash you can
/// diagnose. This box removes the assumption entirely: the semaphore and any result
/// payload live on the heap, independent of any function's stack frame, reference-
/// counted by both sides (the waiter and the completion handler) so whichever side
/// finishes last is the one that frees it - safe even if the completion handler fires
/// arbitrarily long after the waiting function has already returned on a timeout (the
/// exact case a sleeping display now hits routinely: SCShareableContent can take
/// longer than our bounded wait to answer while the display wakes).
typedef struct
{
    dispatch_semaphore_t sem;
    _Atomic(int32_t) refcount; // starts at 2: one for the waiter, one for the completion handler
} msc_wait_box;

static msc_wait_box *msc_wait_box_create(void)
{
    msc_wait_box *box = (msc_wait_box *)calloc(1, sizeof(msc_wait_box));
    box->sem = dispatch_semaphore_create(0);
    atomic_init(&box->refcount, 2);
    return box;
}

/// Call exactly once from each side (waiter and completion handler) when that side is
/// done touching the box. Frees the semaphore and the box itself only once both sides
/// have called this - never while the other side might still be using it.
static void msc_wait_box_release(msc_wait_box *box)
{
    if (box == NULL)
    {
        return;
    }

    int32_t previous = atomic_fetch_sub_explicit(&box->refcount, 1, memory_order_acq_rel);
    if (previous == 1)
    {
        dispatch_release(box->sem);
        free(box);
    }
}

void msc_free_string(char *s)
{
    free(s);
}

void msc_free_devices(msc_device_info *devices, int32_t count)
{
    if (devices == NULL)
    {
        return;
    }
    for (int32_t i = 0; i < count; i++)
    {
        free((void *)devices[i].device_id);
        free((void *)devices[i].name);
    }
    free(devices);
}

#pragma mark - PCM resampling (native format -> 16kHz mono PCM16)

/// Converts CMSampleBuffers of whatever native format a source delivers into 16kHz
/// mono PCM16 blocks via AVAudioConverter (a real anti-aliased polyphase resampler,
/// not naive sample dropping), and hands each converted block to the managed callback.
/// One instance is owned per capture source and reused across the session so the
/// converter (and its internal filter state) is only rebuilt if the input format
/// actually changes mid-session.
@interface MSCPcmResampler : NSObject
@end

@implementation MSCPcmResampler
{
    AVAudioFormat *_outputFormat;
    AVAudioFormat *_inputFormat;
    AVAudioConverter *_converter;
}

- (instancetype)init
{
    self = [super init];
    if (self != nil)
    {
        _outputFormat = [[AVAudioFormat alloc] initWithCommonFormat:AVAudioPCMFormatInt16
                                                           sampleRate:16000.0
                                                             channels:1
                                                          interleaved:YES];
    }
    return self;
}

- (void)dealloc
{
    [_outputFormat release];
    [_inputFormat release];
    [_converter release];
    [super dealloc];
}

- (void)processSampleBuffer:(CMSampleBufferRef)sampleBuffer
                    callback:(msc_audio_callback)cb
               errorCallback:(msc_error_callback)errCb
                    userData:(void *)userData
                     logName:(NSString *)logName
{
    if (!CMSampleBufferDataIsReady(sampleBuffer))
    {
        return;
    }

    CMFormatDescriptionRef formatDesc = CMSampleBufferGetFormatDescription(sampleBuffer);
    if (formatDesc == NULL)
    {
        return;
    }

    const AudioStreamBasicDescription *asbd = CMAudioFormatDescriptionGetStreamBasicDescription(formatDesc);
    if (asbd == NULL)
    {
        return;
    }

    CMItemCount numSamples = CMSampleBufferGetNumSamples(sampleBuffer);
    if (numSamples <= 0)
    {
        return;
    }

    AVAudioFormat *inputFormat = [[AVAudioFormat alloc] initWithStreamDescription:asbd];
    if (inputFormat == nil)
    {
        if (errCb != NULL)
        {
            NSString *msg = [NSString stringWithFormat:@"%@: unsupported native audio format, cannot resample", logName];
            errCb([msg UTF8String], userData);
        }
        return;
    }

    if (_converter == nil || ![_inputFormat isEqual:inputFormat])
    {
        AVAudioConverter *converter = [[AVAudioConverter alloc] initFromFormat:inputFormat toFormat:_outputFormat];
        if (converter == nil)
        {
            if (errCb != NULL)
            {
                NSString *msg = [NSString stringWithFormat:@"%@: could not build resampler for native format %@", logName, inputFormat];
                errCb([msg UTF8String], userData);
            }
            [inputFormat release];
            return;
        }

        [_converter release];
        _converter = converter;

        [_inputFormat release];
        _inputFormat = [inputFormat retain];
    }

    AVAudioPCMBuffer *inputBuffer = [[AVAudioPCMBuffer alloc] initWithPCMFormat:inputFormat frameCapacity:(AVAudioFrameCount)numSamples];
    [inputFormat release];
    if (inputBuffer == nil)
    {
        return;
    }
    inputBuffer.frameLength = (AVAudioFrameCount)numSamples;

    OSStatus copyStatus = CMSampleBufferCopyPCMDataIntoAudioBufferList(
        sampleBuffer, 0, (int32_t)numSamples, inputBuffer.mutableAudioBufferList);
    if (copyStatus != noErr)
    {
        if (errCb != NULL)
        {
            NSString *msg = [NSString stringWithFormat:@"%@: failed to copy PCM data out of sample buffer (OSStatus %d)", logName, (int)copyStatus];
            errCb([msg UTF8String], userData);
        }
        [inputBuffer release];
        return;
    }

    double ratio = _outputFormat.sampleRate / _inputFormat.sampleRate;
    AVAudioFrameCount outCapacity = (AVAudioFrameCount)ceil((double)numSamples * ratio) + 32;
    AVAudioPCMBuffer *outputBuffer = [[AVAudioPCMBuffer alloc] initWithPCMFormat:_outputFormat frameCapacity:outCapacity];
    if (outputBuffer == nil)
    {
        [inputBuffer release];
        return;
    }

    __block BOOL suppliedInput = NO;
    NSError *convError = nil;
    AVAudioConverterOutputStatus status = [_converter
        convertToBuffer:outputBuffer
                  error:&convError
     withInputFromBlock:^AVAudioBuffer *_Nullable(AVAudioPacketCount inNumberOfPackets, AVAudioConverterInputStatus *_Nonnull outStatus) {
        if (suppliedInput)
        {
            *outStatus = AVAudioConverterInputStatus_NoDataNow;
            return nil;
        }
        suppliedInput = YES;
        *outStatus = AVAudioConverterInputStatus_HaveData;
        return inputBuffer;
    }];

    [inputBuffer release];

    if (status == AVAudioConverterOutputStatus_Error)
    {
        if (errCb != NULL)
        {
            NSString *msg = [NSString stringWithFormat:@"%@: resample failed: %@", logName,
                                                         convError.localizedDescription ?: @"unknown error"];
            errCb([msg UTF8String], userData);
        }
        [outputBuffer release];
        return;
    }

    AVAudioFrameCount frames = outputBuffer.frameLength;
    if (frames > 0 && cb != NULL)
    {
        int16_t *samples = outputBuffer.int16ChannelData[0];
        if (samples != NULL)
        {
            cb(samples, (int32_t)frames, userData);
        }
    }

    [outputBuffer release];
}

@end

#pragma mark - Permissions

msc_permission_status msc_mic_permission_status(void)
{
    AVAuthorizationStatus status = [AVCaptureDevice authorizationStatusForMediaType:AVMediaTypeAudio];
    switch (status)
    {
        case AVAuthorizationStatusAuthorized:
            return MSC_PERMISSION_GRANTED;
        case AVAuthorizationStatusNotDetermined:
            return MSC_PERMISSION_NOT_DETERMINED;
        case AVAuthorizationStatusDenied:
        case AVAuthorizationStatusRestricted:
        default:
            return MSC_PERMISSION_DENIED;
    }
}

msc_permission_status msc_request_mic_permission(void)
{
    msc_permission_status current = msc_mic_permission_status();
    if (current != MSC_PERMISSION_NOT_DETERMINED)
    {
        return current;
    }

    // Heap-allocated (msc_wait_box), not a stack __block variable: the consent-sheet
    // completion handler can fire long after this function has already returned on the
    // timeout below (e.g. no WindowServer to show the sheet) - see msc_wait_box's
    // comment for why that makes a plain stack local unsafe to rely on here.
    msc_wait_box *box = msc_wait_box_create();
    __block BOOL granted = NO; // read only after a successful wait; see below
    [AVCaptureDevice requestAccessForMediaType:AVMediaTypeAudio completionHandler:^(BOOL result) {
        granted = result;
        dispatch_semaphore_signal(box->sem);
        msc_wait_box_release(box);
    }];
    // Bounded, not DISPATCH_TIME_FOREVER: on a session with no WindowServer able to
    // display the consent sheet (headless/remote-without-console), the completion
    // handler never fires and an unbounded wait here would hang the calling thread -
    // and therefore msc_mic_start, and therefore MeetingRecorder.Start() - forever,
    // with no way for the caller to recover. Timing out and treating it as denied is
    // the safe default (never silently record); msc_mic_start's caller-facing error
    // message already names the exact System Settings pane either way.
    long waitResult = dispatch_semaphore_wait(box->sem, dispatch_time(DISPATCH_TIME_NOW, (int64_t)(30 * NSEC_PER_SEC)));

    // `&&` short-circuits: `granted` is only read when waitResult == 0, i.e. only after
    // dispatch_semaphore_signal (called before the completion handler touches anything
    // else) has already established a happens-before relationship for it - reading it
    // on the timeout path would otherwise race with a completion handler that might
    // still be running concurrently.
    msc_permission_status result = (waitResult == 0 && granted) ? MSC_PERMISSION_GRANTED : MSC_PERMISSION_DENIED;
    msc_wait_box_release(box);
    return result;
}

msc_permission_status msc_screen_permission_status(void)
{
    // CGPreflightScreenCaptureAccess only distinguishes granted (true) from
    // not-granted (false); macOS does not expose "never asked" vs "explicitly
    // denied" through this API. Callers needing an explicit prompt should call
    // msc_request_screen_permission first.
    return CGPreflightScreenCaptureAccess() ? MSC_PERMISSION_GRANTED : MSC_PERMISSION_DENIED;
}

void msc_request_screen_permission(void)
{
    CGRequestScreenCaptureAccess();
}

#pragma mark - Device enumeration

int32_t msc_list_capture_devices(msc_device_info **out_devices)
{
    if (out_devices == NULL)
    {
        return 0;
    }
    *out_devices = NULL;

    @autoreleasepool
    {
        NSArray<AVCaptureDeviceType> *types = @[ AVCaptureDeviceTypeMicrophone, AVCaptureDeviceTypeExternal ];
        AVCaptureDeviceDiscoverySession *discovery =
            [AVCaptureDeviceDiscoverySession discoverySessionWithDeviceTypes:types
                                                                     mediaType:AVMediaTypeAudio
                                                                      position:AVCaptureDevicePositionUnspecified];
        NSArray<AVCaptureDevice *> *devices = discovery.devices;
        NSString *defaultId = [AVCaptureDevice defaultDeviceWithMediaType:AVMediaTypeAudio].uniqueID;

        NSUInteger count = devices.count;
        if (count == 0)
        {
            return 0;
        }

        msc_device_info *arr = (msc_device_info *)calloc(count, sizeof(msc_device_info));
        NSUInteger i = 0;
        for (AVCaptureDevice *d in devices)
        {
            arr[i].device_id = msc_strdup(d.uniqueID);
            arr[i].name = msc_strdup(d.localizedName);
            arr[i].is_default = (defaultId != nil && [d.uniqueID isEqualToString:defaultId]) ? 1 : 0;
            i++;
        }

        *out_devices = arr;
        return (int32_t)count;
    }
}

/// Render (output/playback) device enumeration via CoreAudio HAL. Informational only -
/// ScreenCaptureKit captures the whole system audio mix and cannot be pointed at a
/// specific output device the way WASAPI loopback can on Windows, but the app UI still
/// wants a real device list to display.
int32_t msc_list_render_devices(msc_device_info **out_devices)
{
    if (out_devices == NULL)
    {
        return 0;
    }
    *out_devices = NULL;

    AudioObjectPropertyAddress devicesAddress = {
        kAudioHardwarePropertyDevices, kAudioObjectPropertyScopeGlobal, kAudioObjectPropertyElementMain
    };

    UInt32 dataSize = 0;
    OSStatus status = AudioObjectGetPropertyDataSize(kAudioObjectSystemObject, &devicesAddress, 0, NULL, &dataSize);
    if (status != noErr || dataSize == 0)
    {
        return 0;
    }

    UInt32 deviceCount = dataSize / (UInt32)sizeof(AudioDeviceID);
    AudioDeviceID *deviceIds = (AudioDeviceID *)malloc(dataSize);
    status = AudioObjectGetPropertyData(kAudioObjectSystemObject, &devicesAddress, 0, NULL, &dataSize, deviceIds);
    if (status != noErr)
    {
        free(deviceIds);
        return 0;
    }

    AudioDeviceID defaultOutputId = kAudioObjectUnknown;
    AudioObjectPropertyAddress defaultOutputAddress = {
        kAudioHardwarePropertyDefaultOutputDevice, kAudioObjectPropertyScopeGlobal, kAudioObjectPropertyElementMain
    };
    UInt32 defaultSize = sizeof(defaultOutputId);
    AudioObjectGetPropertyData(kAudioObjectSystemObject, &defaultOutputAddress, 0, NULL, &defaultSize, &defaultOutputId);

    @autoreleasepool
    {
        NSMutableArray<NSValue *> *outputDeviceIds = [NSMutableArray array];
        for (UInt32 i = 0; i < deviceCount; i++)
        {
            AudioDeviceID deviceId = deviceIds[i];

            AudioObjectPropertyAddress streamsAddress = {
                kAudioDevicePropertyStreams, kAudioDevicePropertyScopeOutput, kAudioObjectPropertyElementMain
            };
            UInt32 streamsSize = 0;
            OSStatus streamStatus = AudioObjectGetPropertyDataSize(deviceId, &streamsAddress, 0, NULL, &streamsSize);
            if (streamStatus != noErr || streamsSize == 0)
            {
                // No output streams on this device (e.g. an input-only device) - not
                // a render device, skip it.
                continue;
            }

            [outputDeviceIds addObject:[NSValue valueWithBytes:&deviceId objCType:@encode(AudioDeviceID)]];
        }

        free(deviceIds);

        NSUInteger count = outputDeviceIds.count;
        if (count == 0)
        {
            return 0;
        }

        msc_device_info *arr = (msc_device_info *)calloc(count, sizeof(msc_device_info));
        NSUInteger written = 0;
        for (NSValue *value in outputDeviceIds)
        {
            AudioDeviceID deviceId;
            [value getValue:&deviceId];

            CFStringRef uidRef = NULL;
            AudioObjectPropertyAddress uidAddress = {
                kAudioDevicePropertyDeviceUID, kAudioObjectPropertyScopeGlobal, kAudioObjectPropertyElementMain
            };
            UInt32 uidSize = sizeof(uidRef);
            if (AudioObjectGetPropertyData(deviceId, &uidAddress, 0, NULL, &uidSize, &uidRef) != noErr || uidRef == NULL)
            {
                continue;
            }

            CFStringRef nameRef = NULL;
            AudioObjectPropertyAddress nameAddress = {
                kAudioDevicePropertyDeviceNameCFString, kAudioObjectPropertyScopeGlobal, kAudioObjectPropertyElementMain
            };
            UInt32 nameSize = sizeof(nameRef);
            AudioObjectGetPropertyData(deviceId, &nameAddress, 0, NULL, &nameSize, &nameRef);

            arr[written].device_id = msc_strdup((__bridge NSString *)uidRef);
            arr[written].name = nameRef != NULL ? msc_strdup((__bridge NSString *)nameRef) : msc_strdup(@"Unknown output device");
            arr[written].is_default = (deviceId == defaultOutputId) ? 1 : 0;
            written++;

            CFRelease(uidRef);
            if (nameRef != NULL)
            {
                CFRelease(nameRef);
            }
        }

        *out_devices = arr;
        return (int32_t)written;
    }
}

#pragma mark - Microphone capture session

@interface MSCMicDelegate : NSObject <AVCaptureAudioDataOutputSampleBufferDelegate>
@property(nonatomic, retain) MSCPcmResampler *resampler;
@property(nonatomic, assign) msc_audio_callback callback;
@property(nonatomic, assign) msc_error_callback errorCallback;
@property(nonatomic, assign) void *userData;
@end

@implementation MSCMicDelegate

- (void)dealloc
{
    [_resampler release];
    [super dealloc];
}

- (void)captureOutput:(AVCaptureOutput *)output
    didOutputSampleBuffer:(CMSampleBufferRef)sampleBuffer
            fromConnection:(AVCaptureConnection *)connection
{
    [self.resampler processSampleBuffer:sampleBuffer
                                callback:self.callback
                           errorCallback:self.errorCallback
                                userData:self.userData
                                 logName:@"mic"];
}

@end

struct msc_mic_session
{
    AVCaptureSession *session;
    AVCaptureDeviceInput *input;
    AVCaptureAudioDataOutput *output;
    MSCMicDelegate *delegate;
    dispatch_queue_t queue;
};

msc_mic_session *msc_mic_start(
    const char *device_id, msc_audio_callback cb, msc_error_callback err_cb, void *user_data, char **out_error)
{
    if (out_error != NULL)
    {
        *out_error = NULL;
    }

    @autoreleasepool
    {
        msc_permission_status perm = msc_request_mic_permission();
        if (perm != MSC_PERMISSION_GRANTED)
        {
            if (out_error != NULL)
            {
                *out_error = strdup(
                    "Microphone access was not granted. Open System Settings > Privacy & Security > "
                    "Microphone, enable MeetingScribe, then try again.");
            }
            return NULL;
        }

        AVCaptureDevice *device = nil;
        if (device_id != NULL)
        {
            NSString *deviceIdStr = [NSString stringWithUTF8String:device_id];
            device = [AVCaptureDevice deviceWithUniqueID:deviceIdStr];
            if (device == nil)
            {
                if (out_error != NULL)
                {
                    NSString *msg = [NSString stringWithFormat:@"Microphone device '%@' was not found.", deviceIdStr];
                    *out_error = msc_strdup(msg);
                }
                return NULL;
            }
        }
        else
        {
            device = [AVCaptureDevice defaultDeviceWithMediaType:AVMediaTypeAudio];
            if (device == nil)
            {
                if (out_error != NULL)
                {
                    *out_error = strdup("No default microphone device is available on this machine.");
                }
                return NULL;
            }
        }

        NSError *error = nil;
        AVCaptureDeviceInput *input = [AVCaptureDeviceInput deviceInputWithDevice:device error:&error];
        if (input == nil)
        {
            if (out_error != NULL)
            {
                NSString *msg = [NSString stringWithFormat:@"Could not open microphone '%@': %@",
                                                             device.localizedName,
                                                             error.localizedDescription ?: @"unknown error"];
                *out_error = msc_strdup(msg);
            }
            return NULL;
        }
        [input retain];

        AVCaptureSession *session = [[AVCaptureSession alloc] init];
        if (![session canAddInput:input])
        {
            if (out_error != NULL)
            {
                *out_error = strdup("Could not attach the selected microphone to a capture session.");
            }
            [input release];
            [session release];
            return NULL;
        }
        [session addInput:input];

        AVCaptureAudioDataOutput *output = [[AVCaptureAudioDataOutput alloc] init];
        MSCMicDelegate *delegate = [[MSCMicDelegate alloc] init];
        delegate.resampler = [[[MSCPcmResampler alloc] init] autorelease];
        delegate.callback = cb;
        delegate.errorCallback = err_cb;
        delegate.userData = user_data;

        dispatch_queue_t queue = dispatch_queue_create("com.meetingscribe.audio.mic-pull", DISPATCH_QUEUE_SERIAL);
        [output setSampleBufferDelegate:delegate queue:queue];

        if (![session canAddOutput:output])
        {
            if (out_error != NULL)
            {
                *out_error = strdup("Could not attach an audio data output to the microphone capture session.");
            }
            [input release];
            [output release];
            [delegate release];
            dispatch_release(queue);
            [session release];
            return NULL;
        }
        [session addOutput:output];

        [session startRunning];

        if (!session.isRunning)
        {
            if (out_error != NULL)
            {
                *out_error = strdup("Microphone capture session failed to start.");
            }
            [input release];
            [output release];
            [delegate release];
            dispatch_release(queue);
            [session release];
            return NULL;
        }

        msc_mic_session *handle = (msc_mic_session *)calloc(1, sizeof(msc_mic_session));
        handle->session = session;
        handle->input = input;
        handle->output = output;
        handle->delegate = delegate;
        handle->queue = queue;
        return handle;
    }
}

void msc_mic_stop(msc_mic_session *session)
{
    if (session == NULL || session->session == nil)
    {
        return;
    }

    [session->session stopRunning];
    // Drain any callback already in flight on the delegate queue before returning, so
    // the caller can safely tear down/flush its WAV writer immediately after Stop().
    dispatch_sync(session->queue, ^{
    });
}

void msc_mic_free(msc_mic_session *session)
{
    if (session == NULL)
    {
        return;
    }

    msc_mic_stop(session);

    [session->output setSampleBufferDelegate:nil queue:NULL];
    [session->input release];
    [session->output release];
    [session->delegate release];
    if (session->queue != NULL)
    {
        dispatch_release(session->queue);
    }
    [session->session release];
    free(session);
}

#pragma mark - System audio capture session (ScreenCaptureKit)

@interface MSCSystemDelegate : NSObject <SCStreamOutput, SCStreamDelegate>
@property(nonatomic, retain) MSCPcmResampler *resampler;
@property(nonatomic, assign) msc_audio_callback callback;
@property(nonatomic, assign) msc_error_callback errorCallback;
@property(nonatomic, assign) void *userData;
@end

@implementation MSCSystemDelegate

- (void)dealloc
{
    [_resampler release];
    [super dealloc];
}

- (void)stream:(SCStream *)stream didOutputSampleBuffer:(CMSampleBufferRef)sampleBuffer ofType:(SCStreamOutputType)type
{
    if (type != SCStreamOutputTypeAudio)
    {
        return;
    }

    [self.resampler processSampleBuffer:sampleBuffer
                                callback:self.callback
                           errorCallback:self.errorCallback
                                userData:self.userData
                                 logName:@"system"];
}

- (void)stream:(SCStream *)stream didStopWithError:(NSError *)error
{
    if (error != nil && self.errorCallback != NULL)
    {
        NSString *msg = [NSString stringWithFormat:@"system audio stream stopped unexpectedly: %@",
                                                     error.localizedDescription ?: @"unknown error"];
        self.errorCallback([msg UTF8String], self.userData);
    }
}

@end

struct msc_system_session
{
    SCStream *stream;
    MSCSystemDelegate *delegate;
    dispatch_queue_t queue;
    IOPMAssertionID displaySleepAssertionID;
    BOOL displaySleepAssertionHeld;
    id screensDidSleepObserver;
    id screensDidWakeObserver;
};

#pragma mark - Power management (keep the display awake during system-audio capture)

/// ScreenCaptureKit needs a shareable display; if the display idle-sleeps mid-meeting
/// the system-audio track silently loses coverage. Holding this assertion for the
/// lifetime of the capture session prevents that. Failure to acquire it is not fatal -
/// it just means the display can sleep as it would without this feature - so this
/// returns NO rather than surfacing an error; the caller decides whether that matters.
static BOOL msc_acquire_display_sleep_assertion(IOPMAssertionID *out_assertion_id)
{
    IOPMAssertionID assertionId = kIOPMNullAssertionID;
    IOReturn status = IOPMAssertionCreateWithName(
        kIOPMAssertionTypePreventUserIdleDisplaySleep,
        kIOPMAssertionLevelOn,
        CFSTR("MeetingScribe system-audio capture"),
        &assertionId);

    if (status != kIOReturnSuccess)
    {
        *out_assertion_id = kIOPMNullAssertionID;
        return NO;
    }

    *out_assertion_id = assertionId;
    return YES;
}

/// Releases the display-sleep assertion held for `session`, if any. Guarded by
/// `displaySleepAssertionHeld` so this is safe to call more than once (msc_system_stop
/// is invoked once directly by callers and once more from inside msc_system_free - a
/// second IOPMAssertionRelease on the same ID would be a bug otherwise).
static void msc_release_display_sleep_assertion(msc_system_session *session)
{
    if (session == NULL || !session->displaySleepAssertionHeld)
    {
        return;
    }

    IOPMAssertionRelease(session->displaySleepAssertionID);
    session->displaySleepAssertionID = kIOPMNullAssertionID;
    session->displaySleepAssertionHeld = NO;
}

/// Nudges a sleeping display awake. Returns immediately - does not wait for the wake to
/// actually complete; the caller is responsible for a bounded wait before relying on
/// the display/SCShareableContent being usable again.
static void msc_wake_display(void)
{
    IOPMAssertionID activityAssertionId = kIOPMNullAssertionID;
    IOPMAssertionDeclareUserActivity(
        CFSTR("MeetingScribe waking display for system-audio capture"),
        kIOPMUserActiveLocal,
        &activityAssertionId);
}

/// Registers for display-sleep/wake notifications for the duration of `session` so a
/// display that goes to sleep *during* an already-running capture is surfaced through
/// the same err_cb the caller already listens to, rather than the system-audio track
/// silently continuing to "succeed" while producing silence.
static void msc_add_screen_sleep_observers(msc_system_session *session, msc_error_callback errCb, void *userData)
{
    NSNotificationCenter *center = [[NSWorkspace sharedWorkspace] notificationCenter];

    id sleepObserver = [center addObserverForName:NSWorkspaceScreensDidSleepNotification
                                             object:nil
                                              queue:nil
                                         usingBlock:^(NSNotification *note) {
        if (errCb != NULL)
        {
            NSString *msg = @"Display went to sleep during system-audio capture; the "
                             @"system-audio track may go silent until the display wakes.";
            errCb([msg UTF8String], userData);
        }
    }];
    session->screensDidSleepObserver = [sleepObserver retain];

    // Wake is informational only - no err_cb needed. ScreenCaptureKit resumes
    // delivering real audio blocks on its own once the display is awake again; the
    // observer only exists here so it can be symmetrically added/removed with sleep.
    id wakeObserver = [center addObserverForName:NSWorkspaceScreensDidWakeNotification
                                            object:nil
                                             queue:nil
                                        usingBlock:^(NSNotification *note) {
    }];
    session->screensDidWakeObserver = [wakeObserver retain];
}

static void msc_remove_screen_sleep_observers(msc_system_session *session)
{
    if (session == NULL)
    {
        return;
    }

    NSNotificationCenter *center = [[NSWorkspace sharedWorkspace] notificationCenter];

    if (session->screensDidSleepObserver != nil)
    {
        [center removeObserver:session->screensDidSleepObserver];
        [session->screensDidSleepObserver release];
        session->screensDidSleepObserver = nil;
    }

    if (session->screensDidWakeObserver != nil)
    {
        [center removeObserver:session->screensDidWakeObserver];
        [session->screensDidWakeObserver release];
        session->screensDidWakeObserver = nil;
    }
}

#pragma mark - Output mute/volume check (ScreenCaptureKit captures the post-mix stream)

/// ScreenCaptureKit's audio output is the post-mix system output stream: if the
/// default output device is muted (or its volume is at 0), every sample captured is
/// silence and macOS still reports the capture as having started successfully - there
/// is no native-side error for this condition. Surface it as a non-fatal warning
/// through the same err_cb the caller already listens to for other runtime issues, so
/// system.wav coming out all-zero doesn't look like a clean, successful recording.
static void msc_warn_if_output_muted_or_silent(msc_error_callback errCb, void *userData)
{
    if (errCb == NULL)
    {
        return;
    }

    AudioDeviceID defaultOutputId = kAudioObjectUnknown;
    AudioObjectPropertyAddress defaultOutputAddress = {
        kAudioHardwarePropertyDefaultOutputDevice, kAudioObjectPropertyScopeGlobal, kAudioObjectPropertyElementMain
    };
    UInt32 defaultSize = sizeof(defaultOutputId);
    OSStatus status = AudioObjectGetPropertyData(
        kAudioObjectSystemObject, &defaultOutputAddress, 0, NULL, &defaultSize, &defaultOutputId);
    if (status != noErr || defaultOutputId == kAudioObjectUnknown)
    {
        // No resolvable default output device - nothing to warn about here; a missing
        // output device is a different, unusual situation the normal error path would
        // surface elsewhere.
        return;
    }

    AudioObjectPropertyAddress muteAddress = {
        kAudioDevicePropertyMute, kAudioDevicePropertyScopeOutput, kAudioObjectPropertyElementMain
    };
    UInt32 muted = 0;
    BOOL haveMute = NO;
    if (AudioObjectHasProperty(defaultOutputId, &muteAddress))
    {
        UInt32 muteSize = sizeof(muted);
        haveMute = (AudioObjectGetPropertyData(defaultOutputId, &muteAddress, 0, NULL, &muteSize, &muted) == noErr);
    }

    AudioObjectPropertyAddress volumeAddress = {
        kAudioHardwareServiceDeviceProperty_VirtualMainVolume, kAudioDevicePropertyScopeOutput, kAudioObjectPropertyElementMain
    };
    Float32 volume = -1.0f;
    BOOL haveVolume = NO;
    if (AudioObjectHasProperty(defaultOutputId, &volumeAddress))
    {
        UInt32 volumeSize = sizeof(volume);
        haveVolume = (AudioObjectGetPropertyData(defaultOutputId, &volumeAddress, 0, NULL, &volumeSize, &volume) == noErr);
    }

    BOOL isMuted = haveMute && muted != 0;
    BOOL isSilentVolume = haveVolume && volume <= 0.0001f;

    if (isMuted)
    {
        NSString *msg = @"System output is muted; the system-audio track will be silent. "
                         @"Unmute to capture system audio.";
        errCb([msg UTF8String], userData);
    }
    else if (isSilentVolume)
    {
        NSString *msg = @"System output volume is at 0; the system-audio track will be silent. "
                         @"Turn up the volume to capture system audio.";
        errCb([msg UTF8String], userData);
    }
}

#pragma mark - System stream creation (one bounded, synchronous attempt)

/// Heap payload for one msc_attempt_system_stream call: the async completion chain
/// (getShareableContentWithCompletionHandler -> addStreamOutput -> startCapture) can
/// keep running well after the bounded wait below times out - most visibly in exactly
/// the case this file now retries for, a sleeping display taking longer to answer than
/// the wait budget. `claimed` records whether msc_attempt_system_stream itself ended up
/// reading (and taking ownership of) `error`/`stream`; if it never did (timeout), the
/// completion side is responsible for releasing them itself - including stopping a
/// stream that, unluckily, went on to start successfully after we'd already given up on
/// it - rather than leaking a live, still-capturing SCStream with no owner.
typedef struct
{
    msc_wait_box box;
    NSError *error;
    SCStream *stream;
    _Atomic(BOOL) claimed;
} msc_stream_attempt_wait;

/// Call exactly once from each side (the waiting function, and the innermost completion
/// handler that ends the chain) when that side is done with `wait`. `claimedByWaiter`
/// must be YES only when called from msc_attempt_system_stream after it has already
/// copied out `wait->error`/`wait->stream` for itself.
static void msc_stream_attempt_finish(msc_stream_attempt_wait *wait, BOOL claimedByWaiter)
{
    if (claimedByWaiter)
    {
        atomic_store_explicit(&wait->claimed, YES, memory_order_release);
    }

    int32_t previous = atomic_fetch_sub_explicit(&wait->box.refcount, 1, memory_order_acq_rel);
    if (previous != 1)
    {
        return; // the other side is still using `wait` - not ours to free yet
    }

    if (!atomic_load_explicit(&wait->claimed, memory_order_acquire))
    {
        // msc_attempt_system_stream timed out and never claimed these - clean them up
        // ourselves so nothing leaks, including a stream that actually started fine.
        if (wait->stream != nil)
        {
            [wait->stream stopCaptureWithCompletionHandler:^(NSError *stopError) {
                // Nothing to report to - the caller that would have owned this stream
                // gave up long ago; this is best-effort cleanup of an orphan.
            }];
            [wait->stream release];
        }
        if (wait->error != nil)
        {
            [wait->error release];
        }
    }

    dispatch_release(wait->box.sem);
    free(wait);
}

/// Performs exactly one attempt to fetch shareable content and stand up the
/// SCStream, blocking the calling thread up to `timeoutSeconds`. Returns the started,
/// +1-owned stream on success, or nil with *outError set (also +1-owned via retain,
/// matching this file's manual-refcount convention) on failure - including on timeout.
static SCStream *msc_attempt_system_stream(
    MSCSystemDelegate *delegate, dispatch_queue_t queue, NSTimeInterval timeoutSeconds, NSError **outError)
{
    msc_stream_attempt_wait *wait = (msc_stream_attempt_wait *)calloc(1, sizeof(msc_stream_attempt_wait));
    wait->box.sem = dispatch_semaphore_create(0);
    atomic_init(&wait->box.refcount, 2);
    atomic_init(&wait->claimed, NO);

    // `wait` is heap-allocated (not stack __block variables) precisely so this whole
    // completion chain always has somewhere valid to write, even if it runs long after
    // this function has already returned on the bounded-wait timeout below.
    //
    // SCShareableContent is async-only. There is no published "audio-only, no
    // display" SCContentFilter constructor in this SDK (verified against
    // SCStream.h) - macOS 13+'s SCStreamOutputTypeAudio still requires a filter
    // built from a real display. We minimize the video side instead: a 2x2 output
    // surface at ~1fps, no SCStreamOutputTypeScreen output ever added, so no video
    // frames are even delivered to us - only capturesAudio matters here.
    [SCShareableContent getShareableContentWithCompletionHandler:^(SCShareableContent *content, NSError *contentError) {
        if (contentError != nil || content.displays.count == 0)
        {
            wait->error = [(contentError ?: [NSError errorWithDomain:@"MeetingScribe"
                                                                 code:-1
                                                             userInfo:@{
                                                                 NSLocalizedDescriptionKey : @"No shareable display was found to attach system-audio capture to."
                                                             }]) retain];
            dispatch_semaphore_signal(wait->box.sem);
            msc_stream_attempt_finish(wait, NO);
            return;
        }

        SCDisplay *display = content.displays.firstObject;
        SCContentFilter *filter = [[SCContentFilter alloc] initWithDisplay:display excludingWindows:@[]];

        SCStreamConfiguration *config = [[SCStreamConfiguration alloc] init];
        config.width = 2;
        config.height = 2;
        config.minimumFrameInterval = CMTimeMake(1, 1);
        config.queueDepth = 4;
        config.capturesAudio = YES;
        config.sampleRate = 48000;
        config.channelCount = 2;
        config.excludesCurrentProcessAudio = YES;

        SCStream *stream = [[SCStream alloc] initWithFilter:filter configuration:config delegate:delegate];
        [filter release];
        [config release];

        if (stream == nil)
        {
            wait->error = [[NSError errorWithDomain:@"MeetingScribe"
                                                code:-2
                                            userInfo:@{NSLocalizedDescriptionKey : @"SCStream initialization failed."}] retain];
            dispatch_semaphore_signal(wait->box.sem);
            msc_stream_attempt_finish(wait, NO);
            return;
        }

        NSError *addError = nil;
        BOOL added = [stream addStreamOutput:delegate
                                          type:SCStreamOutputTypeAudio
                           sampleHandlerQueue:queue
                                         error:&addError];
        if (!added)
        {
            wait->error = [(addError ?: [NSError errorWithDomain:@"MeetingScribe"
                                                             code:-3
                                                         userInfo:@{
                                                             NSLocalizedDescriptionKey : @"Could not attach the audio output to the ScreenCaptureKit stream."
                                                         }]) retain];
            [stream release];
            dispatch_semaphore_signal(wait->box.sem);
            msc_stream_attempt_finish(wait, NO);
            return;
        }

        [stream startCaptureWithCompletionHandler:^(NSError *startError) {
            if (startError != nil)
            {
                wait->error = [startError retain];
                [stream release];
            }
            else
            {
                wait->stream = stream; // ownership transfers into wait (already +1 from alloc)
            }
            dispatch_semaphore_signal(wait->box.sem);
            msc_stream_attempt_finish(wait, NO);
        }];
    }];

    long waitResult = dispatch_semaphore_wait(wait->box.sem, dispatch_time(DISPATCH_TIME_NOW, (int64_t)(timeoutSeconds * NSEC_PER_SEC)));

    SCStream *resultStream = nil;
    NSError *resultError = nil;

    if (waitResult == 0)
    {
        // Happens-after dispatch_semaphore_signal above, and this read happens while
        // `wait` is still guaranteed alive (see msc_stream_attempt_finish: it can't
        // free `wait` until both sides have finished, and we haven't finished yet) -
        // safe regardless of whether the completion side has already called
        // msc_stream_attempt_finish itself.
        resultStream = wait->stream;
        resultError = wait->error;
        msc_stream_attempt_finish(wait, YES);
    }
    else
    {
        if (outError != NULL)
        {
            NSString *msg = [NSString stringWithFormat:@"Timed out (%.0fs) waiting for ScreenCaptureKit to start the system-audio stream.", timeoutSeconds];
            resultError = [[NSError errorWithDomain:@"MeetingScribe" code:-4 userInfo:@{NSLocalizedDescriptionKey : msg}] retain];
        }
        // Not claimed: msc_stream_attempt_finish will clean up wait->error/wait->stream
        // itself, whenever the still-pending completion handler eventually finishes.
        msc_stream_attempt_finish(wait, NO);
    }

    if (outError != NULL)
    {
        *outError = resultError; // +1-owned either way (retain above, or transferred from wait->error); caller owns it now
    }
    else if (resultError != nil)
    {
        [resultError release];
    }
    return resultStream;
}

static BOOL msc_is_no_shareable_display_error(NSError *error)
{
    return error != nil && [error.domain isEqualToString:@"MeetingScribe"] && error.code == -1;
}

msc_system_session *msc_system_start(msc_audio_callback cb, msc_error_callback err_cb, void *user_data, char **out_error)
{
    if (out_error != NULL)
    {
        *out_error = NULL;
    }

    if (msc_screen_permission_status() != MSC_PERMISSION_GRANTED)
    {
        if (out_error != NULL)
        {
            *out_error = strdup(
                "Screen & System Audio Recording permission was not granted. Open System Settings > "
                "Privacy & Security > Screen & System Audio Recording, enable MeetingScribe, then try again "
                "(you must fully quit and relaunch the app after granting it).");
        }
        return NULL;
    }

    @autoreleasepool
    {
        MSCSystemDelegate *delegate = [[MSCSystemDelegate alloc] init];
        delegate.resampler = [[[MSCPcmResampler alloc] init] autorelease];
        delegate.callback = cb;
        delegate.errorCallback = err_cb;
        delegate.userData = user_data;

        dispatch_queue_t queue = dispatch_queue_create("com.meetingscribe.audio.system-pull", DISPATCH_QUEUE_SERIAL);

        IOPMAssertionID displaySleepAssertionID = kIOPMNullAssertionID;
        BOOL displaySleepAssertionHeld = msc_acquire_display_sleep_assertion(&displaySleepAssertionID);

        NSError *error = nil;
        SCStream *stream = msc_attempt_system_stream(delegate, queue, 15.0, &error);

        if (stream == nil && msc_is_no_shareable_display_error(error))
        {
            // The display is very likely asleep - SCShareableContent reports zero
            // displays in that state. Nudge it awake and give macOS a bounded moment
            // to bring the display list back before retrying once.
            [error release];
            error = nil;
            msc_wake_display();
            usleep((useconds_t)(1.5 * 1000 * 1000));
            stream = msc_attempt_system_stream(delegate, queue, 15.0, &error);
        }

        if (stream == nil)
        {
            if (out_error != NULL)
            {
                if (msc_is_no_shareable_display_error(error))
                {
                    *out_error = strdup(
                        "Could not start system-audio capture: no shareable display was found. The display "
                        "must be awake to start system-audio capture - wake the display and try again.");
                }
                else
                {
                    NSString *msg = [NSString stringWithFormat:@"Could not start system-audio capture: %@",
                                                                 error.localizedDescription ?: @"unknown error"];
                    *out_error = msc_strdup(msg);
                }
            }
            [error release];
            [delegate release];
            dispatch_release(queue);
            if (displaySleepAssertionHeld)
            {
                IOPMAssertionRelease(displaySleepAssertionID);
            }
            return NULL;
        }

        [error release];

        msc_system_session *handle = (msc_system_session *)calloc(1, sizeof(msc_system_session));
        handle->stream = stream;
        handle->delegate = delegate;
        handle->queue = queue;
        handle->displaySleepAssertionID = displaySleepAssertionID;
        handle->displaySleepAssertionHeld = displaySleepAssertionHeld;
        msc_add_screen_sleep_observers(handle, err_cb, user_data);

        msc_warn_if_output_muted_or_silent(err_cb, user_data);

        return handle;
    }
}

void msc_system_stop(msc_system_session *session)
{
    if (session == NULL)
    {
        return;
    }

    if (session->stream != nil)
    {
        // Heap-allocated box, not a stack dispatch_semaphore_t local: if this 10s wait
        // times out, stopCaptureWithCompletionHandler's block can still fire later and
        // must always have somewhere valid to signal/release - see msc_wait_box.
        msc_wait_box *box = msc_wait_box_create();
        [session->stream stopCaptureWithCompletionHandler:^(NSError *error) {
            dispatch_semaphore_signal(box->sem);
            msc_wait_box_release(box);
        }];
        dispatch_semaphore_wait(box->sem, dispatch_time(DISPATCH_TIME_NOW, (int64_t)(10 * NSEC_PER_SEC)));
        msc_wait_box_release(box);

        // Drain any callback already in flight on the delegate queue before returning.
        dispatch_sync(session->queue, ^{
        });
    }

    // Both of these are internally guarded (flag / nil-checks) so calling
    // msc_system_stop a second time - as msc_system_free does below - is a no-op here.
    msc_release_display_sleep_assertion(session);
    msc_remove_screen_sleep_observers(session);
}

void msc_system_free(msc_system_session *session)
{
    if (session == NULL)
    {
        return;
    }

    msc_system_stop(session);

    [session->stream release];
    [session->delegate release];
    if (session->queue != NULL)
    {
        dispatch_release(session->queue);
    }
    free(session);
}
