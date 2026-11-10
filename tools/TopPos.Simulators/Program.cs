using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Extensions.Logging;
using TopPos.LockerSimulator;
using TopPos.ServerSimulator;

namespace TopPos.Simulators
{
	/// <summary>
	/// Simulatori servera i paketomata bez GUI-ja, za E2E i ručni rad na Linuxu i Windows-u. Konfiguracija je ista kao za v2
	/// simulatore (<c>app-configuration.json</c> ServerSimulator-a, odnosno LockerSimulator-a).
	///   TopPos.Simulators server [--config &lt;putanja&gt;]
	///   TopPos.Simulators locker [--config &lt;putanja&gt;]
	///   TopPos.Simulators both --server-config &lt;putanja&gt; --locker-config &lt;putanja&gt;
	///   --parent-pid &lt;pid&gt;: gašenje kad nestane roditeljski proces (E2E harness koji je pao ne ostavlja simulatore)
	/// Port 0 u konfiguraciji = slobodan port. Kad su simulatori spremni, na stdout ide jedan red
	/// <c>READY {"server":{"port":..},"locker":{"tcpPort":..,"controlApiPort":..}}</c>; log ide na stderr (i u fajl iz serilog.json).
	/// Gašenje: SIGTERM, SIGINT (Ctrl+C), SIGQUIT ili kraj roditelja. Izlazni kod: 0 uredno, 1 argumenti ili konfiguracija,
	/// 2 port je zauzet.
	/// </summary>
	public static class Program
	{
		public const int ExitOk = 0;
		public const int ExitUsage = 1;
		public const int ExitPortInUse = 2;

		private const string Usage =
			"Upotreba: TopPos.Simulators server|locker [--config <putanja>] [--parent-pid <pid>]\n" +
			"          TopPos.Simulators both --server-config <putanja> --locker-config <putanja> [--parent-pid <pid>]";

		public static async Task<int> Main(string[] args)
		{
			// log i READY red čita harness kao UTF-8 (Windows konzola bi inače koristila kodnu stranu)
			Console.OutputEncoding = System.Text.Encoding.UTF8;
			Log.Logger = CreateLogger();
			try
			{
				return await Run(args).ConfigureAwait(false);
			}
			finally
			{
				await Log.CloseAndFlushAsync().ConfigureAwait(false);
			}
		}

		private static async Task<int> Run(string[] args)
		{
			var logger = new SerilogLoggerFactory(Log.Logger).CreateLogger("TopPos.Simulators");

			if (!TryParse(args, out var options, out var error))
			{
				Console.Error.WriteLine(error);
				Console.Error.WriteLine(Usage);
				return ExitUsage;
			}

			ServerSimulatorConfiguration? serverConfig = null;
			LockerSimulatorConfiguration? lockerConfig = null;
			try
			{
				if (options.ServerConfig != null)
					serverConfig = ReadConfiguration<ServerSimulatorConfiguration>(options.ServerConfig);
				if (serverConfig != null)
					Validate(serverConfig);
				if (options.LockerConfig != null)
					lockerConfig = ReadConfiguration<LockerSimulatorConfiguration>(options.LockerConfig);
				if (lockerConfig != null)
					Validate(lockerConfig);
			}
			catch (Exception ex)
			{
				Log.Error("Konfiguracija nije ispravna: {Message}", ex.Message);
				return ExitUsage;
			}

			using var shutdown = new CancellationTokenSource();
			var signals = new[] { PosixSignal.SIGTERM, PosixSignal.SIGINT, PosixSignal.SIGQUIT }
				.Select(signal => PosixSignalRegistration.Create(signal, context =>
				{
					context.Cancel = true;
					Log.Information("Signal {Signal}: gašenje", context.Signal);
					shutdown.Cancel();
				}))
				.ToList();

			HttpServer? server = null;
			LockerEngine? locker = null;
			try
			{
				try
				{
					if (serverConfig != null)
					{
						server = new HttpServer(serverConfig, logger);
						server.Start();
					}
					if (lockerConfig != null)
						locker = new LockerEngine(lockerConfig, logger);
				}
				catch (Exception ex) when (IsPortInUse(ex))
				{
					Log.Error("Port je zauzet: {Message}", ex.Message);
					return ExitPortInUse;
				}
				catch (Exception ex)
				{
					// npr. nepodržan kontroler (faza B) ili neispravna adresa; port je obrađen iznad
					Log.Error(ex, "Simulatori nisu pokrenuti: {Message}", ex.Message);
					return ExitUsage;
				}

				if (options.ParentPid is int parentPid)
					_ = ExitWithParent(parentPid, shutdown);

				Console.Out.WriteLine("READY " + JsonSerializer.Serialize(new
				{
					server = server == null ? null : new { port = server.Port },
					locker = locker == null ? null : new { tcpPort = locker.TcpPort, controlApiPort = locker.ControlApiPort },
				}));
				Console.Out.Flush();

				try
				{
					await Task.Delay(Timeout.Infinite, shutdown.Token).ConfigureAwait(false);
				}
				catch (OperationCanceledException) { }
				return ExitOk;
			}
			finally
			{
				server?.Stop();
				if (locker != null)
					await locker.DisposeAsync().ConfigureAwait(false);
				foreach (var signal in signals)
					signal.Dispose();
				Log.Information("Simulatori su ugašeni");
			}
		}

		private static async Task ExitWithParent(int parentPid, CancellationTokenSource shutdown)
		{
			try
			{
				using var parent = Process.GetProcessById(parentPid);
				await parent.WaitForExitAsync(shutdown.Token).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				return;
			}
			catch (ArgumentException)
			{
				// proces sa tim PID-om ne postoji (već je završio)
			}
			Log.Information("Roditeljski proces {Pid} je završio: gašenje", parentPid);
			shutdown.Cancel();
		}

		private static bool IsPortInUse(Exception ex)
			=> ex is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse or SocketError.AccessDenied }
				|| ex is HttpListenerException;

		private static void Validate(ServerSimulatorConfiguration config)
		{
			Require(config.Server != null, "nedostaje sekcija Server");
			RequirePort(config.Server!.Port, "Server.Port");
			Require(config.DoorCount >= 1, "DoorCount mora biti bar 1");
			Require(config.PenPos != null && config.CourierQrCodes != null, "PenPos i CourierQrCodes ne smeju biti null");
		}

		private static void Validate(LockerSimulatorConfiguration config)
		{
			Require(config.Locker != null, "nedostaje sekcija Locker");
			Require(config.LockController != null, "nedostaje sekcija LockController");
			Require(config.Locker!.ModuleCount >= 1 && config.Locker.BoxCountPerModule is >= 1 and <= 24, "Locker: ModuleCount bar 1, BoxCountPerModule 1–24");
			Require(config.LockController!.NetworkModule != null, "LockController.NetworkModule ne sme biti null");
			if (config.LockController.TcpController != null)
				RequirePort(config.LockController.TcpController.Port, "LockController.TcpController.Port");
			if (config.ControlApi != null)
				RequirePort(config.ControlApi.Port, "ControlApi.Port");
		}

		private static void RequirePort(int port, string name)
			=> Require(port is >= 0 and <= 65535, $"{name} mora biti 0–65535 (0 = slobodan port)");

		private static void Require(bool condition, string message)
		{
			if (!condition)
				throw new InvalidDataException(message);
		}

		private static T ReadConfiguration<T>(string path)
		{
			var json = File.ReadAllText(path);
			return JsonSerializer.Deserialize<T>(json) ?? throw new InvalidDataException($"{path}: prazna konfiguracija");
		}

		private static Serilog.ILogger CreateLogger()
		{
			// serilog.json uz program (kao v2 simulatori); bez njega samo stderr
			var path = Path.Combine(AppContext.BaseDirectory, "serilog.json");
			if (File.Exists(path))
			{
				var configuration = new ConfigurationBuilder().AddJsonFile(path, optional: false, reloadOnChange: false).Build();
				return new LoggerConfiguration().ReadFrom.Configuration(configuration).Enrich.FromLogContext().CreateLogger();
			}
			return new LoggerConfiguration().MinimumLevel.Information().WriteTo.Console(standardErrorFromLevel: Serilog.Events.LogEventLevel.Verbose).CreateLogger();
		}

		private sealed record Options(string? ServerConfig, string? LockerConfig, int? ParentPid);

		private static bool TryParse(string[] args, out Options options, out string? error)
		{
			options = new Options(null, null, null);
			error = null;
			if (args.Length == 0)
			{
				error = "Nedostaje režim (server, locker ili both).";
				return false;
			}
			var mode = args[0].ToLowerInvariant();
			string? config = null, serverConfig = null, lockerConfig = null;
			int? parentPid = null;
			for (int i = 1; i < args.Length; i++)
			{
				var name = args[i];
				if (i + 1 >= args.Length)
				{
					error = $"Argument {name} nema vrednost.";
					return false;
				}
				var value = args[++i];
				switch (name)
				{
					case "--config": config = value; break;
					case "--server-config": serverConfig = value; break;
					case "--locker-config": lockerConfig = value; break;
					case "--parent-pid":
						if (!int.TryParse(value, out var pid))
						{
							error = $"Neispravan PID: {value}";
							return false;
						}
						parentPid = pid;
						break;
					default:
						error = $"Nepoznat argument: {name}";
						return false;
				}
			}
			switch (mode)
			{
				case "server":
					options = new Options(config ?? serverConfig ?? "app-configuration.json", null, parentPid);
					return true;
				case "locker":
					options = new Options(null, config ?? lockerConfig ?? "app-configuration.json", parentPid);
					return true;
				case "both":
					if (serverConfig == null || lockerConfig == null)
					{
						error = "Režim both traži --server-config i --locker-config.";
						return false;
					}
					options = new Options(serverConfig, lockerConfig, parentPid);
					return true;
				default:
					error = $"Nepoznat režim: {args[0]}";
					return false;
			}
		}
	}
}
