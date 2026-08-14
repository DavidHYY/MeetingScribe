#ifndef MEETINGSCRIBE_MAC_AUDIO_H
#define MEETINGSCRIBE_MAC_AUDIO_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/// One real audio endpoint. `device_id` is a stable identifier: for capture (input)
/// devices this is the AVCaptureDevice uniqueID; for render (output) devices this is
/// the CoreAudio AudioDeviceUID. Strings are UTF-8, owned by the array and freed by
/// msc_free_devices.
typedef struct
{
    const char *device_id;
    const char *name;
    int32_t is_default;
} msc_device_info;

/// TCC permission status, mirrored for both microphone and screen-recording checks.
typedef enum
{
    MSC_PERMISSION_NOT_DETERMINED = 0,
    MSC_PERMISSION_GRANTED = 1,
    MSC_PERMISSION_DENIED = 2,
} msc_permission_status;

/// Delivers one block of interleaved PCM16 mono samples at 16kHz. Called on a private
/// dispatch queue owned by the native session - the managed side must not block here.
typedef void (*msc_audio_callback)(const int16_t *samples, int32_t sample_count, void *user_data);

/// Reports a non-fatal runtime error (device unplugged, stream interrupted, etc.)
/// after a session has already started successfully. `message` is a transient UTF-8
/// C string valid only for the duration of the call.
typedef void (*msc_error_callback)(const char *message, void *user_data);

/// Lists active capture (microphone/input) devices. Returns the count and allocates
/// *out_devices (NULL/0 on failure or empty list); free with msc_free_devices.
int32_t msc_list_capture_devices(msc_device_info **out_devices);

/// Lists active render (output/speaker) devices. Returns the count and allocates
/// *out_devices (NULL/0 on failure or empty list); free with msc_free_devices.
int32_t msc_list_render_devices(msc_device_info **out_devices);

void msc_free_devices(msc_device_info *devices, int32_t count);

/// Frees a string returned via an `out_error` out-parameter.
void msc_free_string(char *s);

/// Current microphone authorization status - does not prompt.
msc_permission_status msc_mic_permission_status(void);

/// Blocks the calling thread until the user responds to the system mic-access prompt
/// (or returns immediately if already determined). Safe to call from a background
/// thread; must not be called from the main thread of an app with a run loop that
/// itself needs to pump for the system dialog to appear (call from a non-UI thread).
msc_permission_status msc_request_mic_permission(void);

/// Current screen-recording authorization status (governs ScreenCaptureKit access).
/// Does not prompt. CGPreflightScreenCaptureAccess only distinguishes granted/denied,
/// not "never asked" vs "explicitly denied" - both surface as MSC_PERMISSION_DENIED
/// here until the user grants it in System Settings.
msc_permission_status msc_screen_permission_status(void);

/// Triggers the system screen-recording consent prompt if not yet determined. Returns
/// once the prompt has been shown (not once the user has answered - screen-recording
/// consent in System Settings does not block synchronously like mic access does).
void msc_request_screen_permission(void);

typedef struct msc_mic_session msc_mic_session;
typedef struct msc_system_session msc_system_session;

/// Starts microphone capture. `device_id` may be NULL to use the system default input
/// device. On failure returns NULL and sets *out_error to a malloc'd UTF-8 message
/// (free with msc_free_string); on success *out_error is set to NULL.
msc_mic_session *msc_mic_start(
    const char *device_id,
    msc_audio_callback cb,
    msc_error_callback err_cb,
    void *user_data,
    char **out_error);

/// Stops microphone capture and tears down the session. Safe to call once; a second
/// call on an already-stopped session is a no-op. Does not free the session pointer -
/// call msc_mic_free after.
void msc_mic_stop(msc_mic_session *session);

void msc_mic_free(msc_mic_session *session);

/// Starts system-audio capture via ScreenCaptureKit. On failure returns NULL and sets
/// *out_error to a malloc'd UTF-8 message (free with msc_free_string); on success
/// *out_error is set to NULL. Requires screen-recording permission
/// (msc_screen_permission_status == MSC_PERMISSION_GRANTED).
msc_system_session *msc_system_start(
    msc_audio_callback cb,
    msc_error_callback err_cb,
    void *user_data,
    char **out_error);

void msc_system_stop(msc_system_session *session);

void msc_system_free(msc_system_session *session);

#ifdef __cplusplus
}
#endif

#endif
