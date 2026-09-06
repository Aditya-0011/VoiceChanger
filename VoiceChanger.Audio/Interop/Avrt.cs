using System.Runtime.InteropServices;

namespace VoiceChanger.Audio.Interop;

/// <summary>
/// Multimedia Class Scheduler Service (MMCSS) P/Invoke bindings.
/// </summary>
internal static partial class Avrt
{
    /// <summary>
    /// Associates the calling thread with the specified multimedia task (e.g. "Pro Audio").
    /// </summary>
    /// <param name="taskName">Task name string ("Pro Audio").</param>
    /// <param name="taskIndex">Reference to task index (initialized to 0).</param>
    /// <returns>Handle to the task association, or 0 on failure.</returns>
    [LibraryImport("avrt.dll", EntryPoint = "AvSetMmThreadCharacteristicsW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial nint AvSetMmThreadCharacteristicsW(string taskName, ref uint taskIndex);

    /// <summary>
    /// Relinquishes membership in the multimedia task.
    /// </summary>
    /// <param name="avrtHandle">Handle returned by <see cref="AvSetMmThreadCharacteristicsW"/>.</param>
    /// <returns>True on success, false otherwise.</returns>
    [LibraryImport("avrt.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AvRevertMmThreadCharacteristics(nint avrtHandle);
}
