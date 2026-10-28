using System.Drawing;
using System.Windows.Forms;

namespace Wisp
{
    /// <summary>What a page can ask of the main window.</summary>
    interface IHost
    {
        Form Window { get; }
        void ShowToast(string text, ToastKind kind);
        void UpdateStatus();
        void BeginBusy();
        void EndBusy();
    }

    /// <summary>One tab of the main window.</summary>
    abstract class Page : BufferedPanel, IScalable
    {
        /// <summary>Re-applies sizes set in design pixels and lays the page out again.</summary>
        public virtual void ApplyScale() { }

        protected readonly IHost Host;
        bool activated;

        protected Page(IHost host)
        {
            Host = host;
        }

        protected static int S(int v) { return Theme.S(v); }

        public virtual void ApplyTheme()
        {
            BackColor = Theme.Current.Bg;
            foreach (Control c in Controls)
            {
                var card = c as Card;
                if (card != null) card.ApplyTheme();
            }
        }

        public abstract void ApplyLanguage();

        /// <summary>Called every time the tab is shown; the first time also starts loading.</summary>
        public void Activate()
        {
            if (!activated)
            {
                activated = true;
                FirstActivate();
            }
            OnActivated();
        }

        /// <summary>Starts the first load in the background before the tab is opened, so it opens ready.</summary>
        public void Preload()
        {
            if (activated) return;
            activated = true;
            FirstActivate();
        }

        protected virtual void FirstActivate() { }
        protected virtual void OnActivated() { }

        public virtual string Status { get { return ""; } }
        public virtual void RefreshData() { }
        public virtual void FocusSearch() { }

        /// <summary>Runs on the UI thread and waits; ignored once the window is gone.</summary>
        protected void RunUI(System.Action a)
        {
            try
            {
                if (IsHandleCreated && !IsDisposed) Invoke((MethodInvoker)delegate { a(); });
            }
            catch (System.ObjectDisposedException) { }
            catch (System.InvalidOperationException) { }
        }

        /// <summary>Queues to the UI thread without waiting.</summary>
        protected void Post(System.Action a)
        {
            try
            {
                if (IsHandleCreated && !IsDisposed) BeginInvoke((MethodInvoker)delegate { a(); });
            }
            catch (System.InvalidOperationException) { }
        }

        protected static FlatButton Button(string glyph, ButtonKind kind = ButtonKind.Normal)
        {
            return new FlatButton("", glyph, kind);
        }

        protected static void SetButton(FlatButton b, string text)
        {
            b.Text = text;
            b.Width = b.PreferredWidth;
        }

        protected static SmoothLabel Heading()
        {
            return new SmoothLabel { AutoSize = false, Font = Theme.HeadFont, TextAlign = ContentAlignment.MiddleLeft, UseMnemonic = false };
        }
    }
}
