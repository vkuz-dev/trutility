namespace trutility
{
    /// <summary>
    /// Where the hotkey opens the menu. The corners are those of the working
    /// area, so the menu sits beside the taskbar rather than over it, on the
    /// screen the mouse is on.
    /// </summary>
    internal enum MenuPosition
    {
        BottomRight = 0,
        BottomLeft = 1,
        TopRight = 2,
        TopLeft = 3,
        Cursor = 4,
    }
}
