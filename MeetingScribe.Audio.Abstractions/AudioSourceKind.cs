namespace MeetingScribe.Audio;

/// <summary>Which of the two independently-enable-able capture sources an event refers to.</summary>
public enum AudioSourceKind
{
    /// <summary>A physical/virtual input device captured via <c>WasapiCapture</c> — "our side".</summary>
    Microphone,

    /// <summary>A render device captured via <c>WasapiLoopbackCapture</c> — "the remote side" / system audio.</summary>
    SystemAudio,
}
