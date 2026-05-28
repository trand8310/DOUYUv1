using System.Drawing;

namespace MainClient
{
    public sealed class OsrPreviewForm : Form
    {
        private readonly FlowLayoutPanel _screenshotPanel = new()
        {
            AutoScroll = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(8),
            WrapContents = true
        };

        public OsrPreviewForm()
        {
            AutoScaleDimensions = new SizeF(120F, 120F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = "CefClient.OffScreen 预览";
            ClientSize = new Size(500, 1000);
            ShowInTaskbar = true;
            Controls.Add(_screenshotPanel);
        }

        public void ShowScreenshot(string consumerId, string browserId, string screenshotBase64)
        {
            if (IsDisposed || Disposing)
                return;

            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => ShowScreenshot(consumerId, browserId, screenshotBase64)));
                return;
            }

            Image screenshot;
            try
            {
                var screenshotBytes = Convert.FromBase64String(screenshotBase64);
                using var stream = new MemoryStream(screenshotBytes);
                using var image = Image.FromStream(stream);
                screenshot = new Bitmap(image);
            }
            catch
            {
                return;
            }

            EnsurePreviewWindowVisible();

            var item = _screenshotPanel.Controls
                .OfType<Panel>()
                .FirstOrDefault(x => string.Equals(x.Name, GetScreenshotItemName(consumerId), StringComparison.OrdinalIgnoreCase));

            if (item == null)
            {
                item = CreateScreenshotItem(consumerId);
                _screenshotPanel.Controls.Add(item);
                SortScreenshotItemsByConsumerId();
            }

            var title = item.Controls.OfType<Label>().First();
            title.Text = $"Consumer {consumerId}  {browserId}  {DateTime.Now:HH:mm:ss}";

            var pictureBox = item.Controls.OfType<PictureBox>().First();
            var oldImage = pictureBox.Image;
            pictureBox.Image = screenshot;
            oldImage?.Dispose();
        }

        private void EnsurePreviewWindowVisible()
        {
            if (!Visible)
                Show();

            if (WindowState == FormWindowState.Minimized)
                WindowState = FormWindowState.Normal;
        }

        private void SortScreenshotItemsByConsumerId()
        {
            var sortedItems = _screenshotPanel.Controls
                .OfType<Panel>()
                .OrderBy(GetConsumerSortBucket)
                .ThenBy(GetConsumerSortNumber)
                .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            for (var i = 0; i < sortedItems.Count; i++)
                _screenshotPanel.Controls.SetChildIndex(sortedItems[i], i);
        }

        private static int GetConsumerSortBucket(Control control)
        {
            return int.TryParse(GetConsumerIdFromControl(control), out _) ? 0 : 1;
        }

        private static int GetConsumerSortNumber(Control control)
        {
            return int.TryParse(GetConsumerIdFromControl(control), out var consumerId)
                ? consumerId
                : int.MaxValue;
        }

        private static string GetConsumerIdFromControl(Control control)
        {
            if (control.Tag is string consumerId)
                return consumerId;

            const string prefix = "screenshot_consumer_";
            return control.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? control.Name[prefix.Length..]
                : control.Name;
        }

        private static void DisposeControlImages(Control control)
        {
            foreach (var pictureBox in control.Controls.OfType<PictureBox>())
            {
                pictureBox.Image?.Dispose();
                pictureBox.Image = null;
            }

            foreach (Control child in control.Controls)
                DisposeControlImages(child);
        }

        private static string GetScreenshotItemName(string consumerId)
        {
            return $"screenshot_consumer_{consumerId}";
        }

        private static Panel CreateScreenshotItem(string consumerId)
        {
            var item = new Panel
            {
                Name = GetScreenshotItemName(consumerId),
                Tag = consumerId,
                Width = 412,
                Height = 915,
                Margin = new Padding(4),
                BorderStyle = BorderStyle.FixedSingle
            };

            var title = new Label
            {
                AutoEllipsis = true,
                Dock = DockStyle.Top,
                Height = 28,
                Text = $"CefClient {consumerId}",
                TextAlign = ContentAlignment.MiddleCenter
            };

            var pictureBox = new PictureBox
            {
                Dock = DockStyle.Fill,
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.WhiteSmoke
            };

            item.Controls.Add(pictureBox);
            item.Controls.Add(title);
            return item;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (Control control in _screenshotPanel.Controls)
                    DisposeControlImages(control);
            }

            base.Dispose(disposing);
        }
    }
}
