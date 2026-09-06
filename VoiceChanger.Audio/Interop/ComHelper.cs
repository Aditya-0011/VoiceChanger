using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace VoiceChanger.Audio.Interop;

/// <summary>
/// Strategy-based COM wrappers helper for GeneratedComInterface instances.
/// </summary>
internal static class ComHelper
{
    private static readonly StrategyBasedComWrappers s_wrappers = new();

    /// <summary>
    /// Gets or creates a managed wrapper object for a native COM interface pointer.
    /// </summary>
    public static T GetOrCreateObject<T>(nint pUnknown) where T : class
    {
        return (T)s_wrappers.GetOrCreateObjectForComInstance(pUnknown, CreateObjectFlags.None);
    }
}
