using TopPos.Kiosk;
using TopPos.Service;
using Xunit;

namespace TopPos.Kiosk.Tests
{
	/// <summary>
	/// <see cref="AppConfiguration.NormalizeAndValidate"/>: opcione sekcije sa konstantnim sadržajem dobijaju podrazumevane
	/// vrednosti, a vrednosti vezane za uređaj ili tržište se ne izmišljaju (izuzetak sa jasnom porukom).
	/// </summary>
	public class AppConfigurationTests
	{
		/// <summary>Minimalna konfiguracija: samo obavezne sekcije; POS podsekcije (Logging, ProtocolV1, InputV2) nedostaju.</summary>
		private static AppConfiguration Minimal(bool terminalInstalled) => new() {
			General = new GeneralConfiguration(),
			Locker = new LockerConfiguration(),
			TopPosService = new TopPosServiceConfiguration(),
			PosTerminal = new PosTerminalConfiguration { TerminalInstalled = terminalInstalled },
			BarcodeScanner = new BarcodeScannerConfiguration()
		};

		[Fact]
		public void Missing_POS_Logging_and_ProtocolV1_get_defaults_and_are_reported()
		{
			// PosClient ih čita bez provere — starija konfiguracija bez njih ranije je obarala start (NullReferenceException).
			var config = Minimal(terminalInstalled: false);
			var defaulted = config.NormalizeAndValidate();

			Assert.NotNull(config.PosTerminal.Logging);
			Assert.False(config.PosTerminal.Logging!.Enabled);
			Assert.NotNull(config.PosTerminal.ProtocolV1);
			Assert.False(config.PosTerminal.ProtocolV1!.SendRespMessage13);
			Assert.Contains("PosTerminal.Logging", defaulted);
			Assert.Contains("PosTerminal.ProtocolV1", defaulted);
		}

		[Fact]
		public void Existing_POS_subsections_are_kept()
		{
			var config = Minimal(terminalInstalled: true);
			config.PosTerminal.Logging = new LoggingConfiguration { Enabled = true, Detailed = true };
			config.PosTerminal.ProtocolV1 = new ProtocolV1Configuration { SendRespMessage13 = true };
			config.PosTerminal.InputV2 = new InputV2Configuration { AmountCurrency = "941" };

			var defaulted = config.NormalizeAndValidate();

			Assert.True(config.PosTerminal.Logging.Detailed);
			Assert.True(config.PosTerminal.ProtocolV1.SendRespMessage13);
			Assert.DoesNotContain("PosTerminal.Logging", defaulted);
			Assert.DoesNotContain("PosTerminal.ProtocolV1", defaulted);
		}

		[Theory]
		[InlineData(false)]   // sekcija InputV2 ne postoji
		[InlineData(true)]    // sekcija postoji, šifra valute prazna
		public void Installed_terminal_without_currency_code_fails_with_clear_message(bool sectionPresent)
		{
			// Šifra valute zavisi od tržišta — ne izmišlja se, kao LockerId ili COM port.
			var config = Minimal(terminalInstalled: true);
			if (sectionPresent) {
				config.PosTerminal.InputV2 = new InputV2Configuration { AmountCurrency = " " };
			}

			var ex = Assert.Throws<Exception>(() => config.NormalizeAndValidate());
			Assert.Contains("AmountCurrency", ex.Message);
		}

		[Fact]
		public void Missing_agent_communication_defaults_to_disabled_and_is_reported()
		{
			var config = Minimal(terminalInstalled: false);
			var defaulted = config.NormalizeAndValidate();

			Assert.Contains("AgentCommunication", defaulted);
			Assert.False(config.AgentCommunication.Enabled);
			Assert.Equal("TopPos.Kiosk.Agent.v1", config.AgentCommunication.PipeName);
			Assert.Empty(config.AgentCommunication.GetValidationErrors());
		}

		[Theory]
		[InlineData("PipeName")]
		[InlineData("HeartbeatIntervalInMilliseconds")]
		[InlineData("UiAckTimeoutInMilliseconds")]
		[InlineData("HandshakeTimeoutInMilliseconds")]
		[InlineData("WriteTimeoutInMilliseconds")]
		[InlineData("MaxMessageBytes")]
		[InlineData("MaxPendingResponses")]
		public void Invalid_agent_communication_value_is_reported(string field)
		{
			var c = new AgentCommunicationConfiguration { Enabled = true };
			switch (field) {
				case "PipeName": c.PipeName = @"\\.\pipe\x"; break;
				case "HeartbeatIntervalInMilliseconds": c.HeartbeatIntervalInMilliseconds = 70000; break;
				case "UiAckTimeoutInMilliseconds": c.UiAckTimeoutInMilliseconds = c.HeartbeatIntervalInMilliseconds; break;
				case "HandshakeTimeoutInMilliseconds": c.HandshakeTimeoutInMilliseconds = 0; break;
				case "WriteTimeoutInMilliseconds": c.WriteTimeoutInMilliseconds = 120000; break;
				case "MaxMessageBytes": c.MaxMessageBytes = 100; break;
				case "MaxPendingResponses": c.MaxPendingResponses = 0; break;
			}

			var error = Assert.Single(c.GetValidationErrors());
			Assert.Contains($"AgentCommunication.{field}", error);
		}

		/// <summary>v4: putanja socket-a (Linux) — apsolutna, bez .., u granici sun_path.</summary>
		[Theory]
		[InlineData("")]
		[InlineData("run/toppos-kiosk/agent.v1.sock")]
		[InlineData("/run/toppos-kiosk/")]
		[InlineData("/run/toppos-kiosk/../agent.v1.sock")]
		[InlineData("/run/toppos-kiosk/agent-ime-koje-je-predugacko-za-sun-path-jer-linux-dozvoljava-samo-sto-sedam-bajtova-sa-zavrsnom-nulom.sock")]
		public void Invalid_socket_path_is_reported(string socketPath)
		{
			var c = new AgentCommunicationConfiguration { Enabled = true, SocketPath = socketPath };

			Assert.Contains("AgentCommunication.SocketPath", Assert.Single(c.GetValidationErrors()));
		}

		/// <summary>v4: proverava se samo adresa transporta platforme — neispravna adresa drugog ne isključuje kanal.</summary>
		[Fact]
		public void Address_of_the_unused_transport_is_not_checked()
		{
			var c = new AgentCommunicationConfiguration { Enabled = true, PipeName = "", SocketPath = "" };

			Assert.Empty(c.GetValidationErrors(checkPipeName: false, checkSocketPath: false));
			Assert.Contains("SocketPath", Assert.Single(c.GetValidationErrors(checkPipeName: false, checkSocketPath: true)));
			Assert.Contains("PipeName", Assert.Single(c.GetValidationErrors(checkPipeName: true, checkSocketPath: false)));
		}

		[Fact]
		public void Currency_code_is_not_required_without_terminal()
		{
			// Bez terminala se PosClient ne pravi, pa InputV2 nije potreban.
			var config = Minimal(terminalInstalled: false);
			config.NormalizeAndValidate();
			Assert.Null(config.PosTerminal.InputV2);
		}
	}
}
