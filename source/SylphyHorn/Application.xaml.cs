using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Principal;
using System.Windows;
using System.Windows.Threading;
using Livet;
using MetroRadiance.UI;
using MetroTrilithon.Lifetime;
using MetroTrilithon.Threading.Tasks;
using StatefulModel;
using SylphyHorn.Serialization;
using SylphyHorn.Services;
using SylphyHorn.UI;
using WindowsDesktop;

namespace SylphyHorn
{
	sealed partial class Application : IDisposableHolder
	{
		public static bool IsWindowsBridge { get; }
#if APPX
			= true;
#else
			= false;
#endif

		private readonly MultipleDisposable _compositeDisposable = new MultipleDisposable();

		internal HookService HookService { get; private set; }

		internal TaskTrayIcon TaskTrayIcon { get; private set; }

		private static bool IsAdministrator()
		{
			var identity = WindowsIdentity.GetCurrent();
			var principal = new WindowsPrincipal(identity);
			return principal.IsInRole(WindowsBuiltInRole.Administrator);
		}

		protected override void OnStartup(StartupEventArgs e)
		{
			Args = new CommandLineArgs(e.Args);

			if (Args.Setup)
			{
				this.SetupShortcut();
			}

			if (Settings.General.StartAsAdmin && !IsAdministrator())
			{
				var exeName = Process.GetCurrentProcess().MainModule.FileName;
				var startInfo = new ProcessStartInfo(exeName)
				{
					Verb = "runas",
					Arguments = string.Join(" ", e.Args)
				};
				Process.Start(startInfo);
				Current.Shutdown();
				return;
			}

#if !DEBUG
			var appInstance = new MetroTrilithon.Desktop.ApplicationInstance().AddTo(this);
			if (appInstance.IsFirst || Args.Restarted.HasValue)
#endif
			{
				if (WindowsDesktop.VirtualDesktop.IsSupported)
				{
					this.ShutdownMode = ShutdownMode.OnExplicitShutdown;
					DispatcherHelper.UIDispatcher = this.Dispatcher;

					this.DispatcherUnhandledException += this.HandleDispatcherUnhandledException;
					TaskLog.Occured += (sender, log) => LoggingService.Instance.Register(log);

					LocalSettingsProvider.Instance.LoadAsync().Wait();

					Settings.General.Culture.Subscribe(x => ResourceService.Current.ChangeCulture(x)).AddTo(this);
					ThemeService.Current.Register(this, Theme.Windows, Accent.Windows);

					this.HookService = new HookService().AddTo(this);

					var preparation = new ApplicationPreparation(this.HookService, this.Shutdown, this);
					this.TaskTrayIcon = preparation.CreateTaskTrayIcon().AddTo(this);
					this.TaskTrayIcon.Show();

					if (Settings.General.FirstTime)
					{
						preparation.CreateFirstTimeBaloon().Show();

						Settings.General.FirstTime.Value = false;
						LocalSettingsProvider.Instance.SaveAsync().Forget();
					}

					preparation.VirtualDesktopInitialized += () => this.TaskTrayIcon.Reload();
					preparation.VirtualDesktopInitialized += () =>
					{
						SettingsHelper.ResizeSettingsProperties();
						VirtualDesktop.Created += (sender, args) => SettingsHelper.ResizeSettingsProperties();
						VirtualDesktop.DestroyBegin += (sender, args) => SettingsHelper.RemoveDesktopNameEntry(VirtualDesktopService.CachedNumber);

						VirtualDesktop.Created += VirtualDesktopService.DesktopCreatedHandler;
						VirtualDesktop.Destroyed += VirtualDesktopService.DesktopDestroyedHandler;
						VirtualDesktop.CurrentChanged += VirtualDesktopService.DesktopSwitchedHandler;

						VirtualDesktopService.VirtualDesktopInitializedHandler();
					};
					preparation.VirtualDesktopInitializationCanceled += () => { }; // ToDo
					preparation.VirtualDesktopInitializationFailed += ex => LoggingService.Instance.Register(ex);
					preparation.PrepareVirtualDesktop();
					preparation.RegisterActions();

					NotificationService.Instance.AddTo(this);
					RenameService.Instance.AddTo(this);
					WallpaperService.Instance.AddTo(this);
					AlwaysOnTopService.Instance.AddTo(this);

#if !DEBUG
					appInstance.CommandLineArgsReceived += (sender, message) =>
					{
						var args = new CommandLineArgs(message.CommandLineArgs);
						if (args.Setup) this.SetupShortcut();
					};
#endif

					base.OnStartup(e);
				}
				else
				{
					MessageBox.Show("This applications is supported only Windows 10 Anniversary Update (build 14393).", "Not supported", MessageBoxButton.OK, MessageBoxImage.Stop);
					this.Shutdown();
				}
			}
#if !DEBUG
			else
			{
				appInstance.SendCommandLineArgs(e.Args);
				this.Shutdown();
			}
#endif
		}

		protected override void OnExit(ExitEventArgs e)
		{
			base.OnExit(e);
			((IDisposable)this).Dispose();
		}

		private void SetupShortcut()
		{
			var startup = new Startup();
			if (!startup.IsExists)
			{
				startup.Create();
			}
		}

		private void HandleDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs args)
		{
			LoggingService.Instance.Register(args.Exception);
			args.Handled = true;
		}

		#region IDisposable members

		ICollection<IDisposable> IDisposableHolder.CompositeDisposable => this._compositeDisposable;

		void IDisposable.Dispose()
		{
			this._compositeDisposable.Dispose();
		}

		#endregion
	}
}
