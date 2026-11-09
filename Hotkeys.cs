using System.Runtime.InteropServices;
using System.Text;

namespace PanelSonidos;

/// <summary>Teclas globales: funcionan aunque la ventana este oculta o haya otro programa delante.</summary>
public sealed class HotkeyManager : NativeWindow, IDisposable
{
    public const uint ModAlt = 1, ModCtrl = 2, ModShift = 4;
    const uint ModNoRepeat = 0x4000;
    const int WmHotkey = 0x0312;

    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    readonly Dictionary<int, Action> actions = new();
    int nextId = 1;

    public HotkeyManager() => CreateHandle(new CreateParams());

    public bool Register(uint mods, uint vk, Action action)
    {
        int id = nextId++;
        if (!RegisterHotKey(Handle, id, mods | ModNoRepeat, vk)) return false;
        actions[id] = action;
        return true;
    }

    public void Clear()
    {
        foreach (var id in actions.Keys) UnregisterHotKey(Handle, id);
        actions.Clear();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmHotkey && actions.TryGetValue(m.WParam.ToInt32(), out var action)) action();
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        Clear();
        DestroyHandle();
    }
}

public static class KeyNames
{
    [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint mapType);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetKeyNameText(int lParam, StringBuilder text, int size);

    public static string Describe(uint mods, uint vk)
    {
        var text = "";
        if ((mods & HotkeyManager.ModCtrl) != 0) text += "Ctrl + ";
        if ((mods & HotkeyManager.ModAlt) != 0) text += "Alt + ";
        if ((mods & HotkeyManager.ModShift) != 0) text += "Shift + ";
        return text + KeyName((Keys)vk);
    }

    static string KeyName(Keys key)
    {
        if (key is >= Keys.NumPad0 and <= Keys.NumPad9) return "Num " + (key - Keys.NumPad0);
        if (key is >= Keys.F1 and <= Keys.F24) return key.ToString();
        if (key is >= Keys.D0 and <= Keys.D9) return ((int)(key - Keys.D0)).ToString();
        switch (key)
        {
            case Keys.Multiply: return "Num *";
            case Keys.Add: return "Num +";
            case Keys.Subtract: return "Num -";
            case Keys.Divide: return "Num /";
            case Keys.Decimal: return "Num .";
        }

        bool extended = key is Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Insert or Keys.Delete
            or Keys.Home or Keys.End or Keys.PageUp or Keys.PageDown;
        int lParam = (int)MapVirtualKey((uint)key, 0) << 16 | (extended ? 1 << 24 : 0);
        var name = new StringBuilder(64);
        return GetKeyNameText(lParam, name, name.Capacity) > 0 ? name.ToString() : key.ToString();
    }
}
