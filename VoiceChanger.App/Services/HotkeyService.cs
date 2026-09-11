using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;

namespace VoiceChanger.App.Services;

/// <summary>
/// Global keyboard shortcut manager using Win32 RegisterHotKey and window subclassing.
/// Allows voice presets to be switched even when a fullscreen game or other application has focus (Phase 3).
/// </summary>
public sealed partial class HotkeyService : IDisposable
{
    private const uint WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;
    private const uint MOD_NOREPEAT = 0x4000;

    private readonly nint _hWnd;
    private readonly DispatcherQueue _dispatcher;
    private readonly SubclassProc _subclassProc;
    private readonly Dictionary<int, string> _idToPresetId = new();
    private int _nextId = 1000;
    private bool _isSubclassed;
    private bool _disposed;

    /// <summary>
    /// Event fired when a registered global hotkey is pressed, passing the bound preset ID.
    /// Dispatched directly to the UI thread.
    /// </summary>
    public event Action<string>? HotkeyPressed;

    private delegate nint SubclassProc(nint hWnd, uint uMsg, nint wParam, nint lParam, nuint uIdSubclass, nint dwRefData);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(nint hWnd, int id);

    [LibraryImport("comctl32.dll", EntryPoint = "SetWindowSubclass")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowSubclass(nint hWnd, SubclassProc pfnSubclass, nuint uIdSubclass, nint dwRefData);

    [LibraryImport("comctl32.dll", EntryPoint = "RemoveWindowSubclass")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RemoveWindowSubclass(nint hWnd, SubclassProc pfnSubclass, nuint uIdSubclass);

    [LibraryImport("comctl32.dll", EntryPoint = "DefSubclassProc")]
    private static partial nint DefSubclassProc(nint hWnd, uint uMsg, nint wParam, nint lParam);

    /// <summary>
    /// Creates a new instance of <see cref="HotkeyService"/>.
    /// </summary>
    /// <param name="hWnd">Window handle of the application main window.</param>
    /// <param name="dispatcher">UI thread dispatcher queue.</param>
    public HotkeyService(nint hWnd, DispatcherQueue dispatcher)
    {
        _hWnd = hWnd;
        _dispatcher = dispatcher;
        _subclassProc = OnSubclassMessage;

        if (_hWnd != 0)
        {
            _isSubclassed = SetWindowSubclass(_hWnd, _subclassProc, 101, 0);
        }
    }

    /// <summary>
    /// Registers a global hotkey shortcut string (e.g. "Ctrl+Shift+1") bound to a preset.
    /// </summary>
    /// <param name="hotkeyString">Hotkey combination string.</param>
    /// <param name="presetId">Preset identifier to activate.</param>
    /// <returns>True if registration succeeded; false otherwise.</returns>
    public bool Register(string hotkeyString, string presetId)
    {
        if (string.IsNullOrWhiteSpace(hotkeyString) || string.IsNullOrWhiteSpace(presetId) || _hWnd == 0)
        {
            return false;
        }

        if (!TryParseHotkey(hotkeyString, out uint modifiers, out uint vk))
        {
            return false;
        }

        int id = ++_nextId;
        bool success = RegisterHotKey(_hWnd, id, modifiers | MOD_NOREPEAT, vk);
        if (success)
        {
            _idToPresetId[id] = presetId;
        }

        return success;
    }

    /// <summary>
    /// Unregisters all active hotkeys.
    /// </summary>
    public void UnregisterAll()
    {
        if (_hWnd == 0)
        {
            return;
        }

        foreach (int id in _idToPresetId.Keys)
        {
            UnregisterHotKey(_hWnd, id);
        }
        _idToPresetId.Clear();
    }

    private nint OnSubclassMessage(nint hWnd, uint uMsg, nint wParam, nint lParam, nuint uIdSubclass, nint dwRefData)
    {
        if (uMsg == WM_HOTKEY)
        {
            int hotkeyId = (int)wParam;
            if (_idToPresetId.TryGetValue(hotkeyId, out string? presetId))
            {
                _dispatcher.TryEnqueue(() =>
                {
                    HotkeyPressed?.Invoke(presetId);
                });
            }
        }

        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    private static bool TryParseHotkey(string hotkeyString, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;

        string[] parts = hotkeyString.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return false;
        }

        for (int i = 0; i < parts.Length - 1; i++)
        {
            string mod = parts[i].ToLowerInvariant();
            if (mod is "ctrl" or "control")
            {
                modifiers |= MOD_CONTROL;
            }
            else if (mod == "shift")
            {
                modifiers |= MOD_SHIFT;
            }
            else if (mod == "alt")
            {
                modifiers |= MOD_ALT;
            }
            else if (mod is "win" or "windows")
            {
                modifiers |= MOD_WIN;
            }
            else
            {
                return false;
            }
        }

        string keyPart = parts[^1].ToUpperInvariant();
        if (keyPart.Length == 1 && keyPart[0] is >= '0' and <= '9')
        {
            vk = (uint)keyPart[0];
            return true;
        }
        if (keyPart.Length == 1 && keyPart[0] is >= 'A' and <= 'Z')
        {
            vk = (uint)keyPart[0];
            return true;
        }
        if (keyPart.StartsWith('F') && int.TryParse(keyPart[1..], out int fNum) && fNum is >= 1 and <= 12)
        {
            vk = (uint)(0x70 + (fNum - 1)); // VK_F1 = 0x70
            return true;
        }

        return false;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        UnregisterAll();

        if (_isSubclassed && _hWnd != 0)
        {
            RemoveWindowSubclass(_hWnd, _subclassProc, 101);
            _isSubclassed = false;
        }
    }
}
