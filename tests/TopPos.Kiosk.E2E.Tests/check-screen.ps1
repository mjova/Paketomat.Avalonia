# Da li prozor klijenta u E2E (ScreenResolution iz Config\client.app-configuration.json) staje ceo u radnu povrsinu
# primarnog ekrana (bez taskbar-a). Isti racun kao Infrastructure\ScreenCheck.cs i MainWindow.CustomizeWindowForDevelopment:
# prozor centriran na ekranu, mere u WPF logickim jedinicama (skaliranje uracunato).
# Izlaz: 0 = staje, 1 = ne staje (razlog na izlazu), 2 = provera nije uspela (sablon, PowerShell) - ekran nije izmeren.
param([Parameter(Mandatory = $true)][string]$Template)

try {
	Add-Type -AssemblyName PresentationFramework

	# Tokeni {{...}} nisu pod navodnicima (npr. "Port": {{ServerPort}}), pa se pre citanja JSON-a zamene brojem.
	$configuration = (Get-Content -Raw -LiteralPath $Template -ErrorAction Stop) -replace '\{\{\w+\}\}', '0' | ConvertFrom-Json
	$width = [double]$configuration.ScreenResolution.Width
	$height = [double]$configuration.ScreenResolution.Height
	if ($width -le 0 -or $height -le 0) {
		throw "ScreenResolution (Width, Height) nije zadat u $Template."
	}

	$screenWidth = [System.Windows.SystemParameters]::PrimaryScreenWidth
	$screenHeight = [System.Windows.SystemParameters]::PrimaryScreenHeight
	$work = [System.Windows.SystemParameters]::WorkArea
	$bounds = New-Object System.Windows.Rect((($screenWidth - $width) / 2), (($screenHeight - $height) / 2), $width, $height)
}
catch {
	Write-Output "Provera ekrana nije uspela: $($_.Exception.Message)"
	exit 2
}

if ($work.Contains($bounds)) {
	exit 0
}
Write-Output ("Prozor klijenta {0}x{1} ne staje na ekran: ekran {2:0}x{3:0}, radna povrsina {4:0}x{5:0} (logicke jedinice)." -f $width, $height, $screenWidth, $screenHeight, $work.Width, $work.Height)
Write-Output "Povecati rezoluciju ili vratiti skaliranje na 100% (Settings > Display)."
exit 1
