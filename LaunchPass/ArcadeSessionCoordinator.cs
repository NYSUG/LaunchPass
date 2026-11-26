using LaunchPass.Catalog;
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.Gaming.Input;
using Windows.Storage;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace RetroPass
{
    internal sealed class ArcadeSessionCoordinator
    {
        private static ArcadeSessionCoordinator current;
        internal static ArcadeSessionCoordinator Current=>current??(current=new ArcadeSessionCoordinator());
        internal static bool ConsumeKey(VirtualKey key,bool down)=>current!=null && current.Key(key,down);
        internal static bool ConsumeBack()=>current!=null && current.Engine.Back(current.clock.Elapsed.TotalMilliseconds);
        internal static void NextTrack(){if(current!=null)current.Next();}
        internal static bool BlockActions=>current!=null&&(current.Engine.Input.AttractActive||current.Engine.Input.Quiet(current.Now));
        internal static Task BeforeLaunchAsync()=>current==null?Task.CompletedTask:current.PrepareLaunch();
        internal static void LaunchFinished(bool activated)
        {
            if(current==null)return;
            current.presenter?.LaunchActivationCompleted(activated);
            if(!activated){current.Policy.Launching=false;current.Resume();}
        }
        internal readonly ArcadeSessionPolicy Policy=new ArcadeSessionPolicy();
        internal readonly ArcadeAttractEngine Engine=new ArcadeAttractEngine();
        internal ArcadeExperienceSettings Settings{get;private set;}=new ArcadeExperienceSettings();
        private readonly Stopwatch clock=Stopwatch.StartNew();
        private readonly DispatcherTimer timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(100)};
        private readonly CoreWindow window;
        private readonly ArcadeRadioController radio;
        private ArcadeLightingDirector lighting;
        private PremiumBrowserPage presenter;
        private bool initialized,scanning,ticking;
        private Task initialization;
        internal double Now=>clock.Elapsed.TotalMilliseconds;
        internal event Action<string,string> ExperienceChanged;
        internal bool LightingEnabled => lighting != null;
        internal void BlackoutLighting() => lighting?.Blackout();
        internal void SelectionChanged(string id)
        {
            lighting?.SelectionChanged(id, Now);
            ExperienceChanged?.Invoke("selection", id);
        }
        private ArcadeSessionCoordinator()
        {
            window=Window.Current.CoreWindow;Policy.Foreground=Window.Current.Visible;
            radio=new ArcadeRadioController(()=>new ArcadeMediaDeck(window.Dispatcher),()=>Now);
            radio.Diagnostic+=message=>StartupLog.Write("Tim's Arcade Radio: "+message);
            window.Activated+=(sender,args)=>
            {
                Policy.Foreground=args.WindowActivationState!=CoreWindowActivationState.Deactivated;
                if(!Policy.Foreground){Engine.Reset(Now);radio.Suspend();}
                else{Policy.Launching=false;Engine.Reset(Now);Resume();}
            };
            window.VisibilityChanged+=(sender,args)=>{if(!args.Visible){Policy.Foreground=false;Engine.Reset(Now);radio.Suspend();}};
            window.Dispatcher.AcceleratorKeyActivated+=(sender,args)=>
            {
                bool down=args.EventType==CoreAcceleratorKeyEventType.KeyDown||args.EventType==CoreAcceleratorKeyEventType.SystemKeyDown;
                bool up=args.EventType==CoreAcceleratorKeyEventType.KeyUp||args.EventType==CoreAcceleratorKeyEventType.SystemKeyUp;
                if((down||up)&&Key(args.VirtualKey,down))args.Handled=true;
            };
            window.KeyDown+=(sender,args)=>{if(Key(args.VirtualKey,true))args.Handled=true;};
            window.KeyUp+=(sender,args)=>{if(Key(args.VirtualKey,false))args.Handled=true;};
            Application.Current.EnteredBackground+=(sender,args)=>{Policy.Foreground=false;Engine.Reset(Now);radio.Suspend();};
            Application.Current.LeavingBackground+=(sender,args)=>{Policy.Foreground=true;Policy.Launching=false;Engine.Reset(Now);Resume();};
            Application.Current.Suspending+=(sender,args)=>{Policy.Foreground=false;Engine.Reset(Now);radio.Suspend();};
            Engine.Event+=name=>
            {
                if(name=="attract-enter"||name=="attract-exit")StartupLog.Write("Arcade museum: "+name);
                presenter?.ExperienceEvent(name);ExperienceChanged?.Invoke(name,Engine.CurrentGame);
            };
            timer.Tick+=(sender,args)=>Tick();timer.Start();
        }
        internal Task InitializeAsync()=>initialization??(initialization=Initialize());
        private async Task Initialize()
        {
            try
            {
                var file=await ApplicationData.Current.LocalFolder.TryGetItemAsync("ArcadeExperience.json") as StorageFile;
                if(file!=null)
                {
                    if((await file.GetBasicPropertiesAsync()).Size>64*1024)throw new FormatException("Settings exceed 64 KiB.");
                    Settings=ArcadeExperienceSettings.Parse(await FileIO.ReadTextAsync(file));
                }
            }
            catch(Exception ex){Settings=new ArcadeExperienceSettings();StartupLog.Write("ArcadeExperience settings rejected; safe defaults: "+ex.Message);}
            await InitializeLightingAsync();
            StartupLog.Write("ArcadeExperience effective settings: music="+Settings.Music.Enabled+
                "; attract="+Settings.Attract.Enabled+"; idleSeconds="+Settings.Attract.IdleSeconds+"; cycleSeconds="+Settings.Attract.CycleSeconds+"; restart required after editing JSON");
            initialized=true;radio.Configure(Settings.Music);Resume();
        }
        private async Task InitializeLightingAsync()
        {
            try
            {
                var file = await ApplicationData.Current.LocalFolder.TryGetItemAsync("ArcadeLighting.json") as StorageFile;
                if (file == null) return;
                if ((await file.GetBasicPropertiesAsync()).Size > 4096) throw new FormatException("Settings exceed 4 KiB.");
                var settings = ArcadeLightingSettings.Parse(await FileIO.ReadTextAsync(file));
                if (!settings.Enabled)
                {
                    LogLighting("Disabled by configuration.");
                    return;
                }
                lighting = new ArcadeLightingDirector(new ArcadeLightingClient(settings, LogLighting), LogLighting);
                LogLighting("Enabled at " + settings.Endpoint + ", timeout " + settings.Timeout.TotalMilliseconds + " ms.");
            }
            catch (Exception ex) { LogLighting("Settings unavailable; lighting disabled: " + ex.Message); }
        }
        private static void LogLighting(string message) => StartupLog.Write("Arcade lighting: " + message);
        internal void Attach(PremiumBrowserPage page){presenter=page;Engine.Reset(Now);Resume();}
        internal void Detach(PremiumBrowserPage page){if(ReferenceEquals(presenter,page)){Engine.Reset(Now);presenter=null;}}
        private bool Key(VirtualKey key,bool down)
        {
            if(!initialized||!Policy.Foreground)return false;
            return Engine.Key((int)key,down,Now);
        }
        private async void Resume()
        {
            if(!initialized||scanning||!Settings.Music.Enabled||!Policy.Foreground||Policy.Launching)return;
            scanning=true;
            try
            {
                var frame=Window.Current.Content as Frame;
                if(!((App)Application.Current).InternalCatalogMode||!(frame?.Content is Page)||frame.Content is InternalStorageProbePage)return;
                var folder=await ApplicationData.Current.LocalFolder.TryGetItemAsync("Music") as StorageFolder;
                var tracks=folder==null?new string[0]:(await folder.GetFilesAsync()).Where(file=>file.FileType.Equals(".mp3",StringComparison.OrdinalIgnoreCase)).Select(file=>file.Name).ToArray();
                radio.SetLibrary(tracks);
                if(Policy.Foreground&&!Policy.Launching)await radio.ActivateAsync();
            }
            catch(Exception ex){StartupLog.Write("Radio remains silent: "+ex.Message);radio.Suspend();}
            finally{scanning=false;}
        }
        private async void Next(){if(BlockActions)return;try{await radio.NextAsync();}catch(Exception ex){StartupLog.Write("Radio next: "+ex.Message);}}
        private async Task PrepareLaunch()
        {
            Policy.Launching=true;Engine.Reset(Now);
            try{await radio.FadeOutAndSuspendAsync();}catch(Exception ex){StartupLog.Write("Radio launch fade: "+ex.Message);radio.Suspend();}
        }
        private void Tick()
        {
            if(ticking||!initialized)return;ticking=true;
            try
            {
                var frame=Window.Current.Content as Frame;
                Policy.InternalFrontend=((App)Application.Current).InternalCatalogMode&&frame?.Content is Page&&!(frame.Content is InternalStorageProbePage);
                Policy.PremiumVisible=presenter!=null&&ReferenceEquals(frame?.Content,presenter);
                if (lighting != null && Policy.PremiumVisible && Policy.Foreground) PumpLighting();
                Policy.Ready=Policy.PremiumVisible&&presenter.ExperienceReady;
                Policy.Dialog=Policy.PremiumVisible&&presenter.ExperienceDialog;
                presenter?.TickLaunchCover(Now,Policy.Foreground);
                if(Settings.Attract.Enabled&&Policy.CanAttract)
                {
                    foreach(var pad in Gamepad.Gamepads)
                    {
                        var reading=pad.GetCurrentReading();
                        if(reading.Buttons!=GamepadButtons.None||Math.Abs(reading.LeftThumbstickX)>.25||Math.Abs(reading.LeftThumbstickY)>.25||Math.Abs(reading.RightThumbstickX)>.25||Math.Abs(reading.RightThumbstickY)>.25||reading.LeftTrigger>.2||reading.RightTrigger>.2)
                        {if(Engine.Input.AttractActive || Engine.Input.Quiet(Now))Engine.Activity(Now);else Engine.Input.Touch(Now);}
                    }
                }
                Engine.Tick(Now,Policy.CanAttract,Settings.Attract);
                if(!Policy.CanPlayMusic && radio.Active)radio.Suspend();
                if(Policy.CanPlayMusic && Settings.Music.Enabled && !radio.Active)Resume();
                if(Policy.CanPlayMusic)PumpRadio();
            }
            catch(Exception ex){StartupLog.Write("Arcade experience tick: "+ex.Message);}
            finally{ticking=false;}
        }
        private async void PumpRadio()
        {
            try { await radio.TickAsync(); }
            catch(Exception ex) { StartupLog.Write("Radio tick: "+ex.Message);radio.Suspend(); }
        }
        private async void PumpLighting()
        {
            try { await lighting.TickAsync(Now); }
            catch (Exception ex) { LogLighting("Command failed: " + ex.Message); }
        }
    }
}
