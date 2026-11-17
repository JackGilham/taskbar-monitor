using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using TaskbarMonitor.BLL;

using System.Diagnostics;
using System.Drawing;
using static System.Windows.Forms.VisualStyles.VisualStyleElement;

namespace TaskbarMonitor
{
    public class Taskbar
    {
        public Taskbar(bool ismain = false)
        {
            this.IsMainTaskbar = ismain;
        }
        public bool IsMainTaskbar { get; protected set; }
        public IntPtr TargetWnd = IntPtr.Zero;
        public IntPtr TrayWnd = IntPtr.Zero;
        public IntPtr ClockWnd = IntPtr.Zero;
        public SystemWatcherControl TaskbarMonitorControl;        
        public Rectangle PreviousRect = Rectangle.Empty;
    }
    public class TaskbarManager: IDisposable
    {
        private const int intervalToMonitorTaskbars = 4000;

        public Monitor Monitor { get; private set; }                

        List<Taskbar> TaskbarList = new List<Taskbar>();
        Taskbar MainTaskbar = null;        

        List<IntPtr> g_hook = new List<IntPtr>();
        List<GCHandle> gchs = new List<GCHandle>();

        readonly System.Windows.Forms.Timer timer;
        Dictionary<string, string> monitorIdentities = new Dictionary<string, string>();
        bool reconciling;
        bool disposed;

        public SystemWatcherControl MainControl
        {
            get
            {
                return MainTaskbar?.TaskbarMonitorControl;
            }
        }

        private TaskbarManager()
        {            
            timer = new System.Windows.Forms.Timer { Interval = intervalToMonitorTaskbars };
            timer.Tick += Timer_Tick;
            Options opt = TaskbarMonitor.Options.ReadFromDisk();
            this.Monitor = Monitor.GetInstance(opt);
            this.Monitor.OnOptionsUpdated += Monitor_OnOptionsUpdated;                
            // Start even if Explorer or its secondary taskbars are not ready at logon.
            timer.Start();
        }

        private void Timer_Tick(object sender, EventArgs e)
        {
            AddControlsToTaskbars();
        }

        private void Monitor_OnOptionsUpdated()
        {
            monitorIdentities = MonitorIdentity.Capture();
            for(int i = 0; i < TaskbarList.Count; i++)
            {
                var item = TaskbarList[i];
                UpdatePosition(item, true);                
                
            }
        }

        public void UpdateAllPositions()
        {
            foreach (var item in TaskbarList)
            {
                UpdatePosition(item, true);
            }            
        }

        private void UpdatePosition(Taskbar taskbar, bool force = false)
        {
            if (taskbar.TaskbarMonitorControl == null || taskbar.TaskbarMonitorControl.IsDisposed || !taskbar.TaskbarMonitorControl.IsHandleCreated)
            {
                return;
            }
            

            var handle = taskbar.TargetWnd;
            Rectangle rect = BLL.Win32Api.GetWindowSize(handle);           
            Rectangle offset = Rectangle.Empty;
            if (taskbar.TaskbarMonitorControl != null)
            {
                if (taskbar.IsMainTaskbar)
                {
                    if (taskbar.TrayWnd != IntPtr.Zero)
                    {
                        offset = BLL.Win32Api.GetWindowSize(taskbar.TrayWnd);
                        // offset.Width += 20;
                    }
                }
                else
                {
                    if (taskbar.ClockWnd != IntPtr.Zero)
                    {
                        offset = BLL.Win32Api.GetWindowSize(taskbar.ClockWnd);
                    }
                    else if (WindowsInformation.IsWindows11_22621())
                    {
                        offset = new Rectangle(0, 0, 100, 0);
                    }
                }
            }
              
            MonitorOptions mopt = GetOptionsForTaskbar(taskbar);
            bool visible = this.Monitor.Options.EnableOnAllMonitors || mopt.Enabled;
            int left = mopt.Position == MonitorOptions.DisplayPosition.RIGHT
                ? rect.Width - taskbar.TaskbarMonitorControl.Width - offset.Width
                : 0;
            if (force || taskbar.PreviousRect != offset ||
                taskbar.TaskbarMonitorControl.Visible != visible || taskbar.TaskbarMonitorControl.Left != left)
            {
                Debug.WriteLine("UpdatePosition");
                taskbar.TaskbarMonitorControl?.Invoke((MethodInvoker)delegate
                {
                    taskbar.TaskbarMonitorControl.Visible = visible;
                    taskbar.TaskbarMonitorControl.Left = left;

                    RECT recDiff = new RECT();
                    recDiff.left = rect.Width - taskbar.TaskbarMonitorControl.Width - taskbar.PreviousRect.Width;
                    recDiff.top = 0;
                    recDiff.right = rect.Width - taskbar.TaskbarMonitorControl.Width - offset.Width;
                    recDiff.bottom = rect.Bottom;

                    int rawsize = Marshal.SizeOf(recDiff);
                    IntPtr ptr = Marshal.AllocHGlobal(rawsize);

                    try
                    {
                        Marshal.StructureToPtr(recDiff, ptr, false);
                        BLL.WindowList.InvalidateRect(taskbar.TargetWnd, ptr, true);
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(ptr);
                    }
                });

            }
            
            taskbar.PreviousRect = offset;
        }
        
        public bool AddControlsToTaskbars()
        {
            if (reconciling)
            {
                return false;
            }
            reconciling = true;
            try
            {
                monitorIdentities = MonitorIdentity.Capture();
                return ReconcileTaskbars();
            }
            finally
            {
                reconciling = false;
            }
        }

        private bool ReconcileTaskbars()
        {
            var everythingOK = true;

            List<WindowInformation> windowListExtended = WindowList.GetAllWindowsExtendedInfo();
            HashSet<IntPtr> currentTaskbars = new HashSet<IntPtr>(windowListExtended
                .Where(window => window.Class == "Shell_TrayWnd" || window.Class == "Shell_SecondaryTrayWnd")
                .Select(window => window.Handle));
            foreach (Taskbar taskbar in TaskbarList.ToArray())
            {
                if (!currentTaskbars.Contains(taskbar.TargetWnd))
                {
                    RemoveControl(taskbar);
                }
            }

            string taskbarClass = "Shell_TrayWnd";
            string trayClass = "TrayNotifyWnd";

            var taskbarArea = windowListExtended.Where(w => w.Class == taskbarClass).SingleOrDefault();
            if (taskbarArea == null) return false;

            var trayArea = taskbarArea.ChildWindows.Where(x => x.Class == trayClass).SingleOrDefault();
            if (trayArea == null) return false;
            
            everythingOK &= AddControlToTaskbar(taskbarArea, trayArea, true);

            if (BLL.WindowsInformation.IsWindows11())
            {
                taskbarClass = "Shell_SecondaryTrayWnd";
                trayClass = "Windows.UI.Composition.DesktopWindowContentBridge";

                var taskbarsAreas = windowListExtended.Where(w => w.Class == taskbarClass).ToList();

                foreach (var tbArea in taskbarsAreas)
                {
                    var clockArea = !WindowsInformation.IsWindows11_22621() ? tbArea.ChildWindows.Where(x => x.Class == trayClass).LastOrDefault() : null;
                    everythingOK &= AddControlToTaskbar(tbArea, clockArea, false);
                }
            }

            if (everythingOK)
            {
               HookEvents();
            }
            return everythingOK;
        }

        private MonitorOptions GetOptionsForTaskbar(Taskbar tb)
        {
            Screen screen = Screen.FromHandle(tb.TargetWnd);
            string identity;
            monitorIdentities.TryGetValue(screen.DeviceName, out identity);
            return this.Monitor.Options.GetMonitorOptions(screen.DeviceName, identity);
        }

        private bool AddControlToTaskbar(WindowInformation taskbarArea, WindowInformation trayArea, bool isMainTaskbar)
        {
            Debug.WriteLine("AddControlToTaskbar");

            Taskbar tb = TaskbarList.Where(x => x.TargetWnd == taskbarArea.Handle).SingleOrDefault();
            if (tb != null && tb.TaskbarMonitorControl != null && !tb.TaskbarMonitorControl.IsDisposed && tb.TaskbarMonitorControl.IsHandleCreated)
            {
                tb.TrayWnd = isMainTaskbar && trayArea != null ? trayArea.Handle : IntPtr.Zero;
                tb.ClockWnd = !isMainTaskbar && trayArea != null ? trayArea.Handle : IntPtr.Zero;
                UpdatePosition(tb);
                return true;
            }
            if (tb != null)
            {
                RemoveControl(tb);
                tb = null;
            }
            if(tb == null)
            {
                tb = new Taskbar(isMainTaskbar);
                TaskbarList.Add(tb);
                tb.TargetWnd = taskbarArea.Handle;
            }

            if(isMainTaskbar && WindowsInformation.IsWindows11_22621())
            {
                //taskbarArea = taskbarArea.ChildWindows.Where(x => x.Class == "Windows.UI.Composition.DesktopWindowContentBridge").SingleOrDefault();
                //taskbarArea = taskbarArea.ChildWindows.Where(x => x.Class == "Windows.UI.Input.InputSite.WindowClass").SingleOrDefault();
            }
            
            
            
            var mopt = GetOptionsForTaskbar(tb);
            //if (!this.Monitor.Options.EnableOnAllMonitors || (mopt != null && !mopt.Enabled))
                //return true;


            if (isMainTaskbar)
            {
                MainTaskbar = tb;
                tb.TrayWnd = trayArea.Handle;
            }       
            else if(trayArea != null)
            {
                tb.ClockWnd = trayArea.Handle;
            }

            var rect = BLL.Win32Api.GetWindowSize(taskbarArea.Handle);
            var rectTray = trayArea != null ? BLL.Win32Api.GetWindowSize(trayArea.Handle) : Rectangle.Empty;

            var taskbarMonitorControl = new SystemWatcherControl(this.Monitor);
            tb.TaskbarMonitorControl = taskbarMonitorControl;
            taskbarMonitorControl.Name = "taskbarMonitorFor" + taskbarArea.Handle;
            // taskbarMonitorControl.Left = rect.Width - taskbarMonitorControl.Width - rectTray.Width;


            BLL.Win32Api.SetParent(taskbarMonitorControl.Handle, taskbarArea.Handle);
            //taskbarMonitorControl.BringToFront();

            if (WindowsInformation.IsWindows11_22621())
            {
                Win32Api.SetWindowLong(taskbarMonitorControl.Handle, Win32Api.GWLParameter.GWL_EXSTYLE, (uint)(0x00000000L | 0x00010000L | 0x00080000 | 0x02000000L | 0x00000020L));
                Win32Api.SetLayeredWindowAttributes(taskbarMonitorControl.Handle, 0, 255, 0x00000001 | 0x00000002);
            }
            //Win32Api.SetWindowPos(taskbarMonitorControl.Handle,  new IntPtr(-1), 0, 0, 0, 0,
            //0x0001 | 0x0002 | 0x0040);
            taskbarMonitorControl.Show();

            UpdatePosition(tb);

            return true;
        }

        public bool ApplyOptions(Options options)
        {
            foreach (var taskbar in this.TaskbarList)
            {
                taskbar.TaskbarMonitorControl.ApplyOptions(options);
            }
            return true;
        }
         
        private void HookEvents()
        {
            if (g_hook.Count > 0)
                return;
            
            Debug.WriteLine("HookEvents");
            Dictionary<AccessibleEvents, BLL.Win32Api.WinEventProc> events = InitializeWinEventToHandlerMap();

            //Hook window close event - close our HoverContorl on Target window close.
            BLL.Win32Api.WinEventProc eventHandler = new BLL.Win32Api.WinEventProc(events[AccessibleEvents.LocationChange].Invoke);

            GCHandle gch = GCHandle.Alloc(eventHandler);
            gchs.Add(gch);

            g_hook.Add(BLL.Win32Api.SetWinEventHook(AccessibleEvents.LocationChange,
       AccessibleEvents.LocationChange, IntPtr.Zero, eventHandler
       , 0, 0, BLL.Win32Api.SetWinEventHookParameter.WINEVENT_OUTOFCONTEXT));

            //Hook window close event - close our HoverContorl on Target window close.
            eventHandler = new BLL.Win32Api.WinEventProc(events[AccessibleEvents.Destroy].Invoke);

            gch = GCHandle.Alloc(eventHandler);
            gchs.Add(gch);

            g_hook.Add(BLL.Win32Api.SetWinEventHook(AccessibleEvents.Destroy,
                AccessibleEvents.LocationChange, IntPtr.Zero, eventHandler
                , 0, 0, BLL.Win32Api.SetWinEventHookParameter.WINEVENT_OUTOFCONTEXT));
        }

        private void UnhookEvents()
        {
            Debug.WriteLine("UnhookEvents");
            foreach (var gh in g_hook)
            {
                BLL.Win32Api.UnhookWinEvent(gh);
            }
            g_hook.Clear();
            foreach (var gch in gchs)
            {
                gch.Free();
            }
            gchs.Clear();
        }
         

        public void RemoveControls()
        {
            if (!disposed)
            {
                timer.Stop();
            }
            Debug.WriteLine("RemoveControls");
            UnhookEvents();
            foreach (Taskbar taskbar in TaskbarList.ToArray())
            {
                RemoveControl(taskbar);
            }
            MainTaskbar = null;
        }

        private void RemoveControl(Taskbar taskbar)
        {
            Debug.WriteLine("RemoveControls");
            if (taskbar.TaskbarMonitorControl != null && !taskbar.TaskbarMonitorControl.IsDisposed)
            {
                // Reconciliation and WinEvent callbacks run on the owning UI thread.
                // Explorer may already have destroyed the HWND, so Invoke is not available.
                taskbar.TaskbarMonitorControl.Hide();
                taskbar.TaskbarMonitorControl.Dispose();
            }

            var ret = BLL.WindowList.InvalidateRect(taskbar.TargetWnd, IntPtr.Zero, true);
            TaskbarList.Remove(taskbar);
            if (MainTaskbar == taskbar)
            {
                MainTaskbar = null;
            }
        }
         
        private Dictionary<AccessibleEvents, BLL.Win32Api.WinEventProc> InitializeWinEventToHandlerMap()
        {
            Dictionary<AccessibleEvents, BLL.Win32Api.WinEventProc> dictionary = new Dictionary<AccessibleEvents, BLL.Win32Api.WinEventProc>
            {
                { AccessibleEvents.LocationChange, new BLL.Win32Api.WinEventProc(this.LocationChangedCallback) },
                { AccessibleEvents.Destroy, new BLL.Win32Api.WinEventProc(this.DestroyCallback) }
            };
            return dictionary;
        }

        private void DestroyCallback(IntPtr winEventHookHandle, AccessibleEvents accEvent, IntPtr windowHandle, int objectId, int childId, uint eventThreadId, uint eventTimeInMilliseconds)
        {            
            if (accEvent == AccessibleEvents.Destroy)
            {
                
                var taskbar = TaskbarList.Where(x => x.TargetWnd == windowHandle).SingleOrDefault();
                if(taskbar != null)
                {
                    RemoveControl(taskbar);
                    // The UI timer retries without blocking Explorer's event callback.
                   
                }
            } 
        }
     
        private void LocationChangedCallback(IntPtr winEventHookHandle, AccessibleEvents accEvent, IntPtr windowHandle, int objectId, int childId, uint eventThreadId, uint eventTimeInMilliseconds)
        {
            if (accEvent == AccessibleEvents.LocationChange)
            {
                var taskbar = TaskbarList.Where(x => x.TrayWnd == windowHandle || x.ClockWnd == windowHandle || x.TargetWnd == windowHandle).FirstOrDefault();
                if (taskbar != null)
                {
                    UpdatePosition(taskbar);
                }
            }
        }
         
        public void Dispose()
        {
            if (disposed)
            {
                return;
            }
            timer.Stop();
            RemoveControls();
            timer.Dispose();
            this.Monitor.OnOptionsUpdated -= Monitor_OnOptionsUpdated;
            disposed = true;
        }
        private static TaskbarManager _instance = null;
        public static TaskbarManager GetInstance()
        {
            if (_instance == null) _instance = new TaskbarManager();
            return _instance;
        }
    }
}
