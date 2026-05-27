using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Drawing.Text;
using Timer = System.Windows.Forms.Timer;

namespace MainClient.LogViewer
{
    public sealed class LogItem
    {
        public DateTime Time { get; set; }
        public LogLevel Level { get; set; }
        public string Message { get; set; } = string.Empty;
        public Color Color { get; set; }

        /// <summary>
        /// 原始按行缓存（已做长度截断）
        /// </summary>
        public string[] CachedLines { get; set; } = Array.Empty<string>();

        public int LineCount => CachedLines.Length == 0 ? 1 : CachedLines.Length;
    }

    public sealed class LogViewerUltra : Control
    {
        private readonly List<LogItem> _logs = new();
        private readonly List<LogItem> _filteredLogs = new();
        private readonly List<LogLineIndex> _logLines = new();
        private readonly ConcurrentQueue<LogItem> _queue = new();
        private readonly object _lock = new();

        private readonly VScrollBar _scrollBar = new();
        private readonly Timer _timer = new();

        private readonly Dictionary<Color, SolidBrush> _brushCache = new();

        private int _lineHeight = 22;
        private bool _autoScroll = true;
        private bool _paused;
        private string _filter = string.Empty;
        private bool _filterDirty = true;
        private int _totalLines;

        private int _selectStartLine = -1;
        private int _selectEndLine = -1;
        private bool _selecting;

        private bool _pendingInvalidate;

        private sealed class LogLineIndex
        {
            public required LogItem Log { get; init; }
            public required int StartLine { get; init; }
            public required int LineCount { get; init; }
        }

        /// <summary>
        /// 控件内最大保留日志条数
        /// </summary>
        public int MaxLogs { get; set; } = 10_000;

        /// <summary>
        /// 单条原始消息最大长度，超出直接截断
        /// </summary>
        public int MaxMessageLength { get; set; } = 4000;

        /// <summary>
        /// 单行最大显示长度，超出直接截断
        /// </summary>
        public int MaxLineLength { get; set; } = 500;

        /// <summary>
        /// 每次 UI Tick 最多刷入多少条，避免一次性处理过多
        /// </summary>
        public int MaxFlushPerTick { get; set; } = 300;

        /// <summary>
        /// 队列过大时，低等级日志可丢弃
        /// </summary>
        public int MaxQueueLength { get; set; } = 5000;

        /// <summary>
        /// 可选：是否在队列过载时丢弃非错误日志
        /// </summary>
        public bool DropLowLevelLogsWhenQueueBusy { get; set; } = true;

        public LogViewerUltra()
        {
            DoubleBuffered = true;
            TabStop = true;

            SetStyle(
                ControlStyles.Selectable |
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.UserPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw,
                true);

            Font = new Font("Microsoft YaHei UI", 9f);

            _scrollBar.Dock = DockStyle.Right;
            _scrollBar.Width = 16;
            _scrollBar.Scroll += (_, __) =>
            {
                _autoScroll = IsNearBottom();
                SafeInvalidate();
            };
            Controls.Add(_scrollBar);

            _timer.Interval = 120;
            _timer.Tick += (_, __) => FlushQueue();
            _timer.Start();

            MouseWheel += LogViewer_MouseWheel;
            MouseDown += LogViewer_MouseDown;
            MouseMove += LogViewer_MouseMove;
            MouseUp += LogViewer_MouseUp;
            KeyDown += LogViewer_KeyDown;
            Resize += (_, __) =>
            {
                UpdateScrollBarSafe();
                SafeInvalidate();
            };
        }

        public void WriteLog(string? message, LogLevel level)
        {
            if (DropLowLevelLogsWhenQueueBusy &&
                _queue.Count > MaxQueueLength &&
                level < LogLevel.Error)
            {
                return;
            }

            var safeMessage = message ?? string.Empty;

            if (safeMessage.Length > MaxMessageLength)
            {
                safeMessage = safeMessage[..MaxMessageLength] + " ...[truncated]";
            }

            var time = DateTime.Now;
            var merged = $"{time:HH:mm:ss} [{level}] {safeMessage}";
            var lines = NormalizeLines(merged);

            var item = new LogItem
            {
                Time = time,
                Level = level,
                Message = safeMessage,
                Color = GetColor(level),
                CachedLines = lines
            };

            _queue.Enqueue(item);
        }

        public void Pause() => _paused = true;

        public void Resume() => _paused = false;

        public void ClearLogs()
        {
            lock (_lock)
            {
                _logs.Clear();
                _filteredLogs.Clear();
                _logLines.Clear();
                _totalLines = 0;
                _filterDirty = false;
            }

            _selectStartLine = -1;
            _selectEndLine = -1;

            UpdateScrollBarSafe();
            SafeInvalidate();
        }

        public string GetAllLogs()
        {
            lock (_lock)
            {
                return string.Join(
                    Environment.NewLine,
                    _logs.Select(x => $"{x.Time:HH:mm:ss} [{x.Level}] {x.Message}"));
            }
        }

        public void SetFilter(string? text)
        {
            var filter = text ?? string.Empty;
            if (string.Equals(_filter, filter, StringComparison.Ordinal))
                return;

            _filter = filter;
            _filterDirty = true;

            ApplyFilterIfNeeded();
            UpdateScrollBarSafe();
            SafeInvalidate();
        }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);

            try
            {
                using var g = CreateGraphics();
                var size = TextRenderer.MeasureText(g, "A", Font);
                _lineHeight = Math.Max(18, size.Height + 4);
            }
            catch
            {
                _lineHeight = 22;
            }

            UpdateScrollBarSafe();
            SafeInvalidate();
        }

        protected override bool IsInputKey(Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.C))
                return true;

            return base.IsInputKey(keyData);
        }

        private string[] NormalizeLines(string content)
        {
            if (string.IsNullOrEmpty(content))
                return new[] { string.Empty };

            var lines = content
                .Replace("\r\n", "\n")
                .Replace('\r', '\n')
                .Split('\n');

            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Length > MaxLineLength)
                {
                    lines[i] = lines[i][..MaxLineLength] + " ...";
                }
            }

            return lines;
        }

        private void FlushQueue()
        {
            if (_paused || IsDisposed || Disposing)
                return;

            bool hadUpdates = false;
            bool needRebuild = false;
            int dequeued = 0;

            while (_queue.TryDequeue(out var log))
            {
                hadUpdates = true;

                lock (_lock)
                {
                    _logs.Add(log);

                    bool matchedFilter = string.IsNullOrEmpty(_filter) ||
                                         log.Message.Contains(_filter, StringComparison.OrdinalIgnoreCase);

                    if (!_filterDirty && matchedFilter)
                    {
                        _filteredLogs.Add(log);

                        int lineCount = Math.Max(1, log.LineCount);
                        _logLines.Add(new LogLineIndex
                        {
                            Log = log,
                            StartLine = _totalLines,
                            LineCount = lineCount
                        });
                        _totalLines += lineCount;
                    }
                    else
                    {
                        needRebuild = true;
                    }

                    if (_logs.Count > MaxLogs)
                    {
                        int removeCount = _logs.Count - MaxLogs;
                        _logs.RemoveRange(0, removeCount);
                        needRebuild = true;
                    }
                }

                dequeued++;
                if (dequeued >= MaxFlushPerTick)
                    break;
            }

            if (!hadUpdates && !_filterDirty)
                return;

            if (needRebuild)
                _filterDirty = true;

            if (_filterDirty)
                ApplyFilterIfNeeded();

            UpdateScrollBarSafe();

            if (_autoScroll)
                ScrollToBottomSafe();

            SafeInvalidate();
        }

        private void ApplyFilterIfNeeded()
        {
            lock (_lock)
            {
                if (!_filterDirty)
                    return;

                _filteredLogs.Clear();

                if (string.IsNullOrEmpty(_filter))
                {
                    _filteredLogs.AddRange(_logs);
                }
                else
                {
                    foreach (var log in _logs)
                    {
                        if (log.Message.Contains(_filter, StringComparison.OrdinalIgnoreCase))
                            _filteredLogs.Add(log);
                    }
                }

                _logLines.Clear();
                _totalLines = 0;

                foreach (var log in _filteredLogs)
                {
                    int lineCount = Math.Max(1, log.LineCount);
                    _logLines.Add(new LogLineIndex
                    {
                        Log = log,
                        StartLine = _totalLines,
                        LineCount = lineCount
                    });
                    _totalLines += lineCount;
                }

                _filterDirty = false;
            }
        }

        private void LogViewer_MouseWheel(object? sender, MouseEventArgs e)
        {
            int delta = e.Delta > 0 ? -3 * _lineHeight : 3 * _lineHeight;
            int newVal = Math.Clamp(_scrollBar.Value + delta, _scrollBar.Minimum, _scrollBar.Maximum);

            _scrollBar.Value = newVal;
            _autoScroll = IsNearBottom();

            SafeInvalidate();
        }

        private void LogViewer_MouseDown(object? sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left)
                return;

            Focus();

            _selectStartLine = HitTestLine(e.Y);
            _selectEndLine = _selectStartLine;
            _selecting = true;

            SafeInvalidate();
        }

        private void LogViewer_MouseMove(object? sender, MouseEventArgs e)
        {
            if (!_selecting)
                return;

            _selectEndLine = HitTestLine(e.Y);
            SafeInvalidate();
        }

        private void LogViewer_MouseUp(object? sender, MouseEventArgs e)
        {
            _selecting = false;
        }

        private void LogViewer_KeyDown(object? sender, KeyEventArgs e)
        {
            if (!e.Control || e.KeyCode != Keys.C)
                return;

            CopySelectedOrAll();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }

        private int HitTestLine(int y)
        {
            int totalLines;
            int scrollValue;

            lock (_lock)
            {
                totalLines = _totalLines;
                scrollValue = _scrollBar.Value;
            }

            if (totalLines <= 0)
                return 0;

            int lineIndex = (y + scrollValue) / _lineHeight;
            return Math.Clamp(lineIndex, 0, totalLines - 1);
        }

        private void UpdateScrollBarSafe()
        {
            if (IsDisposed || Disposing)
                return;

            int totalLines;
            lock (_lock)
            {
                totalLines = _totalLines;
            }

            int viewHeight = Math.Max(1, ClientSize.Height);
            int totalPixels = Math.Max(0, totalLines * _lineHeight);
            int maxValue = Math.Max(0, totalPixels - viewHeight);

            _scrollBar.Minimum = 0;
            _scrollBar.LargeChange = viewHeight;
            _scrollBar.SmallChange = _lineHeight;
            _scrollBar.Maximum = maxValue;

            if (_scrollBar.Value > maxValue)
                _scrollBar.Value = maxValue;
        }

        private void ScrollToBottomSafe()
        {
            int maxValue = Math.Max(_scrollBar.Minimum, _scrollBar.Maximum);
            _scrollBar.Value = maxValue;
            _autoScroll = true;
        }

        private bool IsNearBottom()
        {
            return _scrollBar.Value >= Math.Max(_scrollBar.Minimum, _scrollBar.Maximum - _lineHeight);
        }

        private int FindFirstVisibleLogIndex(List<LogLineIndex> lines, int targetLine)
        {
            int left = 0;
            int right = lines.Count - 1;
            int answer = lines.Count;

            while (left <= right)
            {
                int mid = left + ((right - left) / 2);
                var item = lines[mid];
                int endLine = item.StartLine + item.LineCount - 1;

                if (endLine >= targetLine)
                {
                    answer = mid;
                    right = mid - 1;
                }
                else
                {
                    left = mid + 1;
                }
            }

            return answer;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            try
            {
                e.Graphics.Clear(Color.White);
                e.Graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

                List<LogLineIndex> linesRef;
                int totalLines;
                int scrollValue;

                lock (_lock)
                {
                    linesRef = _logLines;
                    totalLines = _totalLines;
                    scrollValue = _scrollBar.Value;
                }

                if (linesRef.Count == 0 || totalLines <= 0)
                    return;

                int startPixel = scrollValue;
                int viewTopLine = startPixel / _lineHeight;
                int viewTopOffset = startPixel % _lineHeight;
                int visibleLineCount = Math.Max(1, (Height / _lineHeight) + 2);
                int lastVisibleLine = viewTopLine + visibleLineCount;

                bool selectionEnabled = _selectStartLine != -1 && _selectEndLine != -1;
                int selMin = Math.Min(_selectStartLine, _selectEndLine);
                int selMax = Math.Max(_selectStartLine, _selectEndLine);

                int startIndex = FindFirstVisibleLogIndex(linesRef, viewTopLine);

                for (int idx = startIndex; idx < linesRef.Count; idx++)
                {
                    var info = linesRef[idx];
                    if (info.StartLine > lastVisibleLine)
                        break;

                    int endLine = info.StartLine + info.LineCount - 1;
                    if (endLine < viewTopLine)
                        continue;

                    var log = info.Log;
                    var lines = log.CachedLines;

                    for (int i = 0; i < lines.Length; i++)
                    {
                        int currentLine = info.StartLine + i;
                        if (currentLine < viewTopLine)
                            continue;
                        if (currentLine > lastVisibleLine)
                            break;

                        int y = (currentLine - viewTopLine) * _lineHeight - viewTopOffset;
                        if (y >= Height)
                            break;

                        if (selectionEnabled && currentLine >= selMin && currentLine <= selMax)
                        {
                            e.Graphics.FillRectangle(
                                Brushes.LightBlue,
                                0,
                                y,
                                Math.Max(0, Width - _scrollBar.Width),
                                _lineHeight);
                        }

                        e.Graphics.DrawString(
                            lines[i],
                            Font,
                            GetBrush(log.Color),
                            4f,
                            y);
                    }
                }
            }
            catch (OutOfMemoryException)
            {
                // 内存紧张时避免再次抛出导致窗体线程崩掉
                // 这里只做降级处理，不再继续绘制
            }
            catch
            {
                // 避免绘制异常传播到 UI 主循环
            }
        }

        private void CopySelectedOrAll()
        {
            try
            {
                List<LogLineIndex> snapshot;

                lock (_lock)
                {
                    snapshot = _logLines.ToList();
                }

                int min = _selectStartLine;
                int max = _selectEndLine;

                if (min == -1 || max == -1)
                {
                    Clipboard.SetText(GetAllLogs());
                    return;
                }

                if (min > max)
                    (min, max) = (max, min);

                var selectedText = new List<string>(256);

                foreach (var info in snapshot)
                {
                    int endLine = info.StartLine + info.LineCount - 1;
                    if (endLine < min || info.StartLine > max)
                        continue;

                    int from = Math.Max(min - info.StartLine, 0);
                    int to = Math.Min(max - info.StartLine, info.Log.CachedLines.Length - 1);

                    for (int i = from; i <= to; i++)
                    {
                        selectedText.Add(info.Log.CachedLines[i]);
                    }
                }

                Clipboard.SetText(string.Join(Environment.NewLine, selectedText));
            }
            catch
            {
            }
        }

        private Brush GetBrush(Color color)
        {
            if (_brushCache.TryGetValue(color, out var brush))
                return brush;

            var created = new SolidBrush(color);
            _brushCache[color] = created;
            return created;
        }

        private static Color GetColor(LogLevel level)
        {
            return level switch
            {
                LogLevel.Trace => Color.Gray,
                LogLevel.Debug => Color.DarkGray,
                LogLevel.Information => Color.Black,
                LogLevel.Warning => Color.Orange,
                LogLevel.Error => Color.Red,
                LogLevel.Critical => Color.DarkRed,
                _ => Color.Black
            };
        }

        private void SafeInvalidate()
        {
            if (_pendingInvalidate || IsDisposed || Disposing)
                return;

            _pendingInvalidate = true;

            BeginInvoke(new Action(() =>
            {
                _pendingInvalidate = false;

                if (!IsDisposed && !Disposing)
                    Invalidate();
            }));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try
                {
                    _timer.Stop();
                    _timer.Dispose();
                }
                catch
                {
                }

                foreach (var brush in _brushCache.Values)
                {
                    try { brush.Dispose(); } catch { }
                }

                _brushCache.Clear();
            }

            base.Dispose(disposing);
        }
    }
}