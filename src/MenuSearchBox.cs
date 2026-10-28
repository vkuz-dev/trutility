using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace trutility
{
    /// <summary>
    /// A search box at the top of the tray menu.
    ///
    /// While it is empty the menu is the normal tree. Typing swaps the tree for
    /// a flat list of matching files from every level, so something three
    /// submenus deep is one keystroke and Enter away. Clearing the box puts the
    /// tree back exactly as it was - its items are set aside, not rebuilt.
    /// </summary>
    internal sealed class MenuSearchBox
    {
        private const int MaxResults = 25;

        /// How many folder levels of a result's location are shown beside it.
        /// The full location is in the tooltip.
        private const int LocationLevels = 2;

        /// A single character matches nearly everything - "t" is inside every
        /// ".txt" - so the tree stays until there is enough to narrow it down.
        private const int MinQueryLength = 2;

        /// <summary>
        /// The box's width when nothing else asks for more. A menu is as wide
        /// as its widest entry, so a box with a generous fixed width made every
        /// menu wide. It now starts narrow and stretches to whatever the entries
        /// need - a long file name, or a search result with its location.
        /// </summary>
        private const int MinBoxWidth = 120;

        private const int EM_SETCUEBANNER = 0x1501;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        private readonly ContextMenuStrip _menu;
        private readonly ToolStripTextBox _box;
        private readonly IReadOnlyList<SearchEntry> _index;
        private readonly Func<MenuNode, ToolStripMenuItem> _newFileItem;
        private readonly Action<ToolStripItemCollection> _fillIcons;
        private readonly Action<string, bool> _launch;

        private readonly System.Windows.Forms.Timer _debounce;
        private readonly List<ToolStripItem> _treeItems = new List<ToolStripItem>();
        private readonly List<ToolStripItem> _resultItems = new List<ToolStripItem>();
        private List<SearchEntry> _hits = new List<SearchEntry>();
        private readonly int _contentStart;
        private bool _treeShown = true;

        /// The corner of the open menu kept in place while its size changes.
        private MenuAnchor _anchor;

        /// The point the menu is about to be opened from, when that is not the
        /// cursor - a hotkey opening it in a corner of the screen. Cleared
        /// again when the menu closes.
        public Point? OpenedFrom { get; set; }

        /// <summary>
        /// Puts the box at the top of an empty menu. Add the tree items next,
        /// then call CaptureTree so the box knows which items it may swap out.
        /// </summary>
        public MenuSearchBox(ContextMenuStrip menu, IReadOnlyList<SearchEntry> index,
                             Func<MenuNode, ToolStripMenuItem> newFileItem,
                             Action<ToolStripItemCollection> fillIcons,
                             Action<string, bool> launch)
        {
            _menu = menu;
            _index = index ?? new SearchEntry[0];
            _newFileItem = newFileItem;
            _fillIcons = fillIcons;
            _launch = launch;

            // Typing only schedules a search; it runs once the keyboard has been
            // quiet for the interval, which is set each time the menu opens.
            _debounce = new System.Windows.Forms.Timer();
            _debounce.Tick += (s, e) => FlushPending();

            _box = new ToolStripTextBox { AutoSize = false, Width = MinBoxWidth };
            _box.TextChanged += (s, e) => OnTextChanged();
            _box.KeyDown += OnKeyDown;

            // Without this the menu treats Enter, Esc and the arrows as its own
            // navigation keys and they never reach the box.
            _box.TextBox.PreviewKeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Down
                    || e.KeyCode == Keys.Up || e.KeyCode == Keys.Right
                    || (e.KeyCode == Keys.Escape && _box.Text.Length > 0))
                {
                    e.IsInputKey = true;
                }
            };
            _box.TextBox.HandleCreated += (s, e) => SetCueBanner();

            // Only search results carry tooltip text - the full location of one
            // that is shortened - so ordinary menu entries still show none.
            menu.ShowItemToolTips = true;

            menu.Items.Add(_box);
            menu.Items.Add(new ToolStripSeparator());
            _contentStart = menu.Items.Count;

            menu.Opened += (s, e) =>
            {
                _anchor = MenuAnchor.Detect(_menu.Bounds, OpenedFrom ?? Cursor.Position);

                // Read on every open, like the focus setting below.
                _debounce.Interval = Settings.NumberSetting.SearchDelayMs.Value();

                // Off by default, so the menu keeps working like any other with
                // the keyboard; clicking the box is enough to start searching.
                // Read on every open, so changing it needs no rebuild.
                if (Settings.ToggleSetting.FocusSearch.IsOn())
                {
                    _box.Focus();
                }
            };
            menu.Closed += (s, e) =>
            {
                _anchor = null;
                OpenedFrom = null;
            };
            menu.Disposed += (s, e) =>
            {
                _debounce.Dispose();
                DisposeDetached();
            };
        }

        /// Everything added after the box, up to now, is the tree it swaps out.
        public void CaptureTree()
        {
            for (int i = _contentStart; i < _menu.Items.Count; i++)
            {
                _treeItems.Add(_menu.Items[i]);
            }
        }

        /// <summary>
        /// Sizes the box to fill exactly the width the other entries need, so
        /// the entries decide how wide the menu is and the box never does -
        /// unless they need less than the box's minimum.
        ///
        /// A hosted box costs more than its own width: the image margin on its
        /// left and space the menu keeps free on its right. Stretching it to
        /// the full content width ignored that, and pushed the menu wider on
        /// every fit. The overhead is measured rather than assumed, since both
        /// parts vary with DPI and theme.
        /// </summary>
        internal void FitWidth()
        {
            // What the entries alone need: at 1px the box cannot be the widest.
            _box.Width = 1;
            _menu.PerformLayout();
            int entriesWidth = _menu.GetPreferredSize(Size.Empty).Width;

            // Made deliberately the widest, the box shows what it costs on top
            // of its own width.
            _box.Width = entriesWidth;
            _menu.PerformLayout();
            int overhead = _menu.GetPreferredSize(Size.Empty).Width - entriesWidth;

            _box.Width = Math.Max(MinBoxWidth, entriesWidth - overhead);
            _menu.PerformLayout();

            KeepAnchored();
        }

        /// <summary>
        /// A menu whose contents change while it is open keeps its top-left
        /// corner where it was. A tray menu opens above the cursor, so a long
        /// list of results grew it up to the top of the screen, and narrowing
        /// the search then shrank it upward from there - leaving it stranded
        /// far from the tray. The corner that was at the cursor stays put
        /// instead, so the menu grows and shrinks from the tray outward.
        /// </summary>
        private void KeepAnchored()
        {
            if (_anchor == null || !_menu.Visible) return;

            Rectangle screen = Screen.FromPoint(_anchor.Corner).Bounds;
            _menu.Location = _anchor.PlaceMenu(_menu.Size, screen);
        }

        /// Each time the menu opens it starts from the full tree.
        public void Reset()
        {
            _debounce.Stop();
            if (_box.Text.Length > 0)
            {
                _box.Text = "";
            }
            else
            {
                ShowTree();
            }
        }

        /// <summary>
        /// Runs a scheduled search now instead of waiting out the debounce.
        /// Enter and Down both need it: a fast typist pressing Enter would
        /// otherwise launch from the results of an earlier, shorter query.
        /// </summary>
        internal void FlushPending()
        {
            if (!_debounce.Enabled) return;

            _debounce.Stop();
            ShowResults(MenuSearch.Find(_index, _box.Text));
        }

        // ── Switching between tree and results ────────────────────────────────

        /// Going back to the tree is immediate - it is only re-inserting items
        /// already built. Only a real search waits for typing to pause.
        private void OnTextChanged()
        {
            _debounce.Stop();
            if (_box.Text.Trim().Length < MinQueryLength)
            {
                ShowTree();
                return;
            }
            _debounce.Start();
        }

        private void ShowTree()
        {
            _hits = new List<SearchEntry>();
            _menu.SuspendLayout();
            RemoveResults();
            if (!_treeShown)
            {
                int at = _contentStart;
                foreach (ToolStripItem item in _treeItems)
                {
                    _menu.Items.Insert(at++, item);
                }
                _treeShown = true;
            }
            _menu.ResumeLayout();
            FitWidth();
        }

        /// <summary>
        /// Lists up to MaxResults matches. The location of each is shown on the
        /// right, where a keyboard shortcut would go, and the thick line still
        /// separates shared entries from the user's own.
        /// </summary>
        private void ShowResults(List<SearchEntry> hits)
        {
            _hits = hits;
            _menu.SuspendLayout();
            RemoveResults();
            HideTree();

            int shown = 0;
            bool previousWasShared = false;
            foreach (SearchEntry hit in hits)
            {
                if (shown == MaxResults) break;

                if (hit.IsYours && previousWasShared)
                {
                    AddResult(new SectionDivider());
                }

                ToolStripMenuItem item = _newFileItem(hit.Node);
                item.ShortcutKeyDisplayString = MenuSearch.ShortLocation(hit.Location, LocationLevels);
                if (item.ShortcutKeyDisplayString != hit.Location)
                {
                    item.ToolTipText = hit.Location;
                }
                AddResult(item);

                previousWasShared = !hit.IsYours;
                shown++;
            }

            if (hits.Count == 0)
            {
                AddResult(new ToolStripMenuItem("No matches") { Enabled = false });
            }
            else if (hits.Count > MaxResults)
            {
                AddResult(new ToolStripMenuItem((hits.Count - MaxResults) + " more - keep typing")
                {
                    Enabled = false,
                });
            }

            _fillIcons(_menu.Items);
            _menu.ResumeLayout();
            FitWidth();

            // Enter launches the best match anyway, so it is highlighted to
            // show that before Enter is pressed.
            _resultItems.Find(IsStop)?.Select();
        }

        private void AddResult(ToolStripItem item)
        {
            _menu.Items.Insert(_contentStart + _resultItems.Count, item);
            _resultItems.Add(item);
        }

        private void RemoveResults()
        {
            foreach (ToolStripItem item in _resultItems)
            {
                _menu.Items.Remove(item);
                item.Dispose();
            }
            _resultItems.Clear();
        }

        /// Set aside, not disposed: the same items come back when the box empties.
        private void HideTree()
        {
            if (!_treeShown) return;
            foreach (ToolStripItem item in _treeItems)
            {
                _menu.Items.Remove(item);
            }
            _treeShown = false;
        }

        /// The menu only disposes what it currently holds, so tree items set
        /// aside by a search in progress would otherwise leak.
        private void DisposeDetached()
        {
            if (!_treeShown)
            {
                foreach (ToolStripItem item in _treeItems)
                {
                    item.Dispose();
                }
            }
            foreach (ToolStripItem item in _resultItems)
            {
                item.Dispose();
            }
        }

        // ── Keyboard ──────────────────────────────────────────────────────────

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (HandleKey(e.KeyData))
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        }

        /// <summary>
        /// The keyboard never leaves the box, so the box moves the highlight
        /// itself: Up and Down walk every entry of the menu, tree or results,
        /// and wrap at the ends. New results start with the best match
        /// highlighted. Enter launches the highlighted entry, or the best
        /// match when nothing is highlighted, and Shift+Enter launches it
        /// as administrator, the same as Shift+Click. Enter or Right on a
        /// folder opens it. Esc clears the box, and a second Esc closes the
        /// menu as usual.
        /// </summary>
        internal bool HandleKey(Keys keyData)
        {
            Keys key = keyData & Keys.KeyCode;
            bool shift = (keyData & Keys.Shift) != 0;

            if (key == Keys.Enter || key == Keys.Down || key == Keys.Up)
            {
                FlushPending();
            }

            // Always swallowed, or the arrow would move the caret instead.
            if (key == Keys.Down || key == Keys.Up)
            {
                MoveHighlight(key == Keys.Down ? 1 : -1);
                return true;
            }

            ToolStripItem highlighted = Stops().Find(i => i.Selected);
            ToolStripMenuItem folder = highlighted as ToolStripMenuItem;
            if ((key == Keys.Enter || key == Keys.Right) && folder != null && folder.HasDropDownItems)
            {
                // An open submenu is its own window with nothing focused in
                // it, so from here the menu's own keys take over - Left
                // closes it and brings them back to the box.
                folder.ShowDropDown();
                foreach (ToolStripItem item in folder.DropDownItems)
                {
                    if (IsStop(item))
                    {
                        item.Select();
                        break;
                    }
                }
                return true;
            }

            if (key == Keys.Enter && highlighted != null)
            {
                string highlightedPath = highlighted.Tag as string;
                if (highlightedPath == null)
                {
                    highlighted.PerformClick();   // Settings, Exit
                    return true;
                }
                _menu.Close();
                _launch(highlightedPath, shift);
                return true;
            }

            if (key == Keys.Enter && _hits.Count > 0)
            {
                string filePath = _hits[0].Node.FilePath;
                _menu.Close();
                _launch(filePath, shift);
                return true;
            }

            if (key == Keys.Escape && _box.Text.Length > 0)
            {
                _box.Text = "";
                return true;
            }

            if (keyData == (Keys.Control | Keys.Back) || keyData == (Keys.Control | Keys.Delete))
            {
                DeleteWord(key == Keys.Back);
                return true;
            }

            return false;
        }

        private void DeleteWord(bool backward)
        {
            TextBox box = _box.TextBox;
            string text = box.Text;
            int start = box.SelectionStart;
            int end = start + box.SelectionLength;

            if (start == end)
            {
                if (backward)
                {
                    while (start > 0 && char.IsWhiteSpace(text[start - 1]))
                    {
                        start--;
                    }
                    while (start > 0 && !char.IsWhiteSpace(text[start - 1]))
                    {
                        start--;
                    }
                }
                else
                {
                    while (end < text.Length && char.IsWhiteSpace(text[end]))
                    {
                        end++;
                    }
                    while (end < text.Length && !char.IsWhiteSpace(text[end]))
                    {
                        end++;
                    }
                }
            }

            if (start == end)
            {
                return;
            }
            box.Text = text.Remove(start, end - start);
            box.SelectionStart = start;
        }

        private static bool IsStop(ToolStripItem item)
        {
            return item.Enabled && item.CanSelect && !(item is ToolStripSeparator);
        }

        /// Everything under the box the highlight can land on, top to bottom.
        private List<ToolStripItem> Stops()
        {
            List<ToolStripItem> stops = new List<ToolStripItem>();
            for (int i = _contentStart; i < _menu.Items.Count; i++)
            {
                if (IsStop(_menu.Items[i]))
                {
                    stops.Add(_menu.Items[i]);
                }
            }
            return stops;
        }

        /// With nothing highlighted, Down starts at the top and Up at the bottom.
        private void MoveHighlight(int step)
        {
            List<ToolStripItem> stops = Stops();
            if (stops.Count == 0) return;

            int at = stops.FindIndex(i => i.Selected);
            int next = at < 0
                ? (step > 0 ? 0 : stops.Count - 1)
                : (at + step + stops.Count) % stops.Count;
            stops[next].Select();
        }

        private void SetCueBanner()
        {
            // wParam 1 keeps the hint visible while the box has focus, which is
            // always, since the menu opens with the cursor in it.
            SendMessage(_box.TextBox.Handle, EM_SETCUEBANNER, (IntPtr)1, "Search…");
        }
    }
}
