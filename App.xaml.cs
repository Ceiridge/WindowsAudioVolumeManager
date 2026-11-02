using System.Windows;
using System.Windows.Controls;
using H.NotifyIcon;
using WindowsAudioVolumeManager.Audio;
using WindowsAudioVolumeManager.Diagnostics;
using WindowsAudioVolumeManager.Interop;
using WindowsAudioVolumeManager.Persistence;
using WindowsAudioVolumeManager.ViewModels;

namespace WindowsAudioVolumeManager {
	/// <summary>
	/// Composition root: builds the persistence layer, migrates the legacy JSON
	/// config, starts the audio service and wires up hotkey/tray/autostart.
	/// Enforces a single instance per user session; a second instance simply
	/// brings the first one to the front.
	/// </summary>
	public partial class App : Application {
		private const string MutexName = @"Local\WindowsAudioVolumeManager_SingleInstance";
		private const string ShowSignalName = @"Local\WindowsAudioVolumeManager_ShowSignal";

		private Mutex? _singleInstanceMutex;
		private bool _ownsMutex;
		private EventWaitHandle? _showSignal;
		private RegisteredWaitHandle? _showSignalRegistration;

		private Database? _database;
		private DebouncedWriter? _writer;
		private AudioDeviceService? _audioService;
		private HotkeyManager? _hotkeyManager;
		private TaskbarIcon? _trayIcon;
		private MainWindow? _mainWindow;
		private MainViewModel? _mainViewModel;

		protected override void OnStartup(StartupEventArgs e) {
			base.OnStartup(e);

			Logger.Initialize(Database.AppDataDirectory);

			_singleInstanceMutex = new Mutex(true, MutexName, out _ownsMutex);
			_showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignalName);

			if (!_ownsMutex) {
				// Another instance is running: tell it to show its window and exit.
				_showSignal.Set();
				Shutdown();
				return;
			}

			SetupExceptionHandlers();

			try {
				InitializeApplication();
			} catch (Exception exception) {
				Logger.Error("Fatal error during startup", exception);
				MessageBox.Show("Windows Audio Volume Manager could not start:\n\n" + exception.Message,
					"Windows Audio Volume Manager", MessageBoxButton.OK, MessageBoxImage.Error);
				Shutdown(1);
			}
		}

		private void InitializeApplication() {
			_database = new Database();
			_database.Initialize();

			SettingsRepository settingsRepository = new(_database);
			if (!Database.UsesIsolatedProfile) {
				JsonConfigMigrator.MigrateIfNeeded(_database, settingsRepository);
			}

			SessionVolumeRepository sessionRepository = new(_database);
			List<SavedSessionRecord> savedRecords = sessionRepository.GetAllDetailed();
			VolumePolicy policy = new() {
				DefaultVolumeScalar = settingsRepository.GetFloat(SettingsRepository.Keys.DefaultVolumeScalar, Database.UsesIsolatedProfile ? 0.05f : 1f)
			};
			policy.Load(savedRecords.ToDictionary(r => r.Name, r => r.ScalarVolume));
			List<SavedDeviceRecord> deviceRecords = new DeviceVolumeRepository(_database).GetAll();
			policy.LoadDevices(deviceRecords.Select(r => r.Rule));

			_writer = new DebouncedWriter(_database);
			_audioService = new AudioDeviceService(policy);

			SettingsViewModel settings = new(settingsRepository);
			_mainViewModel = new MainViewModel(_audioService, policy, _writer, settings, savedRecords, deviceRecords, Dispatcher);

			_mainWindow = new MainWindow(_mainViewModel, settings);
			MainWindow = _mainWindow;

			// The manager creates the Win32 handle immediately; no arbitrary startup delay needed.
			_hotkeyManager = new HotkeyManager(_mainWindow);
			_mainWindow.HotkeyManager = _hotkeyManager;
			_hotkeyManager.HotkeyPressed += ShowMainWindow;
			ApplyHotkey(settings.HotkeyModifiers, settings.HotkeyKey);

			settings.HotkeyChanged += ApplyHotkey;
			settings.TrayIconEnabledChanged += OnTrayIconEnabledChanged;
			settings.AutostartChanged += enabled => AutostartManager.SetEnabled(enabled);
			settings.FollowDefaultDeviceChanged += follow => _audioService?.SetFollowDefaultDevice(follow);

			// Keep the registry entry in sync in case the executable was moved.
			if (settings.Autostart) {
				AutostartManager.SetEnabled(true);
			}

			if (settings.TrayIconEnabled) {
				CreateTrayIcon();
			}

			_audioService.Start(settingsRepository.Get(SettingsRepository.Keys.SelectedDeviceId),
				settings.FollowDefaultDevice);

			_showSignalRegistration = ThreadPool.RegisterWaitForSingleObject(_showSignal!,
				(_, _) => Dispatcher.BeginInvoke(ShowMainWindow), null, Timeout.Infinite, executeOnlyOnce: false);

			if (!settings.StartMinimized || Environment.GetCommandLineArgs().Contains("--show")) {
				_mainWindow.Show();
			}
		}

		private void SetupExceptionHandlers() {
			DispatcherUnhandledException += (_, args) => {
				Logger.Error("Unhandled UI exception", args.Exception);
				if (_mainViewModel != null) {
					_mainViewModel.StatusText = "Error: " + args.Exception.Message;
				}
				args.Handled = true;
			};

			AppDomain.CurrentDomain.UnhandledException += (_, args) =>
				Logger.Error("Unhandled exception", args.ExceptionObject as Exception);

			TaskScheduler.UnobservedTaskException += (_, args) => {
				Logger.Error("Unobserved task exception", args.Exception);
				args.SetObserved();
			};
		}

		private void ApplyHotkey(uint modifiers, uint key) {
			if (_hotkeyManager == null || _mainViewModel == null) {
				return;
			}

			string description = HotkeyManager.Describe(modifiers, key);
			_mainViewModel.HotkeyStatusText = _hotkeyManager.Register(modifiers, key)
				? "Hotkey: " + description
				: $"Hotkey {description} is already in use by another application!";
		}

		private void ShowMainWindow() => _mainWindow?.ShowFromTray();

		/// <summary>Regular exit path: closing the main window shows the goodbye screen and shuts the app down.</summary>
		private void ExitApplication() {
			if (_mainWindow != null) {
				_mainWindow.ShowGoodbyeAndClose();
			} else {
				Shutdown();
			}
		}

		protected override void OnSessionEnding(SessionEndingCancelEventArgs e) {
			// Don't delay logoff/shutdown with the goodbye screen.
			_mainWindow?.ForceClose();
			base.OnSessionEnding(e);
		}

		private void OnTrayIconEnabledChanged(bool enabled) {
			if (enabled) {
				CreateTrayIcon();
			} else {
				DestroyTrayIcon();
				// Without tray icon AND without working hotkey the window would be
				// unreachable when hidden — bring it back to the taskbar then.
				if (_mainWindow is { IsVisible: false } && _hotkeyManager?.IsRegistered != true) {
					_mainWindow.Show();
					_mainWindow.WindowState = WindowState.Minimized;
				}
			}
		}

		private void CreateTrayIcon() {
			if (_trayIcon != null) {
				return;
			}

			MenuItem openItem = new() { Header = "Open" };
			openItem.Click += (_, _) => ShowMainWindow();

			MenuItem exitItem = new() { Header = "Exit" };
			exitItem.Click += (_, _) => ExitApplication();

			ContextMenu menu = new();
			menu.Items.Add(openItem);
			menu.Items.Add(new Separator());
			menu.Items.Add(exitItem);

			_trayIcon = new TaskbarIcon {
				ToolTipText = "Windows Audio Volume Manager",
				Icon = AppIcon.TrayIcon,
				ContextMenu = menu
			};
			_trayIcon.TrayMouseDoubleClick += (_, _) => ShowMainWindow();
			_trayIcon.ForceCreate();
		}

		private void DestroyTrayIcon() {
			_trayIcon?.Dispose();
			_trayIcon = null;
		}

		protected override void OnExit(ExitEventArgs e) {
			_showSignalRegistration?.Unregister(null);
			DestroyTrayIcon();
			_hotkeyManager?.Dispose();
			_audioService?.Dispose();
			_writer?.Dispose(); // flushes pending changes

			if (_ownsMutex) {
				_singleInstanceMutex?.ReleaseMutex();
			}
			_singleInstanceMutex?.Dispose();
			_showSignal?.Dispose();

			base.OnExit(e);
		}
	}
}
