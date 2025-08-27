# -----------------------------------------------------------------------------
# NetDaemonApps Status Check Script
# Quickly verify the deployment status and configuration
# -----------------------------------------------------------------------------

$Green = "`e[32m"
$Yellow = "`e[33m"
$Red = "`e[31m"
$Blue = "`e[36m"
$Reset = "`e[0m"

Write-Host "${Blue}========================================${Reset}"
Write-Host "${Blue}  NetDaemonApps Status Check${Reset}"
Write-Host "${Blue}========================================${Reset}"
Write-Host ""

# Check project structure
Write-Host "${Yellow}📁 Project Structure:${Reset}"
Write-Host "   Project Root: $PWD"

$files = @(
    "NetDaemonApps.csproj",
    "HomeAssistantGenerated.cs", 
    "Apps\Energy\Battery.cs",
    "Properties\PublishProfiles\NetDaemon5Profile.pubxml"
)

foreach ($file in $files) {
    if (Test-Path $file) {
        Write-Host "   ${Green}✅${Reset} $file"
    } else {
        Write-Host "   ${Red}❌${Reset} $file (missing)"
    }
}
Write-Host ""

# Check tools
Write-Host "${Yellow}🔧 Development Tools:${Reset}"

# Check .NET version
try {
    $dotnetVersion = dotnet --version
    Write-Host "   ${Green}✅${Reset} .NET SDK: $dotnetVersion"
} catch {
    Write-Host "   ${Red}❌${Reset} .NET SDK not found"
}

# Check NetDaemon codegen tool
$toolCheck = dotnet tool list | Select-String "netdaemon.hassclient.codegen"
if ($toolCheck) {
    $version = ($toolCheck -split '\s+')[1]
    Write-Host "   ${Green}✅${Reset} NetDaemon CodeGen: $version"
} else {
    Write-Host "   ${Red}❌${Reset} NetDaemon CodeGen tool not installed"
}
Write-Host ""

# Check connectivity
Write-Host "${Yellow}🌐 Connectivity Check:${Reset}"

# Check Home Assistant
try {
    $ping = Test-Connection -ComputerName "192.168.1.135" -Count 1 -Quiet
    if ($ping) {
        Write-Host "   ${Green}✅${Reset} Home Assistant (192.168.1.135) reachable"
    } else {
        Write-Host "   ${Red}❌${Reset} Home Assistant (192.168.1.135) not reachable"
    }
} catch {
    Write-Host "   ${Red}❌${Reset} Cannot test Home Assistant connectivity"
}

# Check publish directory
$publishDir = "\\192.168.1.135\config\netdaemon5"
if (Test-Path $publishDir -ErrorAction SilentlyContinue) {
    Write-Host "   ${Green}✅${Reset} NetDaemon5 directory accessible"
    
    # Check if our files are there
    $ourFiles = Get-ChildItem $publishDir -Filter "NetDaemonApps.*" -ErrorAction SilentlyContinue
    if ($ourFiles) {
        $latestFile = $ourFiles | Sort-Object LastWriteTime -Descending | Select-Object -First 1
        Write-Host "   ${Green}✅${Reset} Last deployment: $($latestFile.LastWriteTime)"
    }
} else {
    Write-Host "   ${Red}❌${Reset} NetDaemon5 directory not accessible"
}
Write-Host ""

# Check configuration
Write-Host "${Yellow}⚙️ Configuration:${Reset}"

if (Test-Path "appsettings.json") {
    try {
        $config = Get-Content "appsettings.json" | ConvertFrom-Json
        $haHost = $config.HomeAssistant.Host
        $haPort = $config.HomeAssistant.Port
        
        Write-Host "   ${Green}✅${Reset} Settings found: $haHost`:$haPort"
        
        if ($config.HomeAssistant.Token) {
            Write-Host "   ${Green}✅${Reset} Home Assistant token configured"
        } else {
            Write-Host "   ${Red}❌${Reset} Home Assistant token missing"
        }
    } catch {
        Write-Host "   ${Red}❌${Reset} Cannot parse appsettings.json"
    }
} else {
    Write-Host "   ${Red}❌${Reset} appsettings.json not found"
}
Write-Host ""

# Feature status
Write-Host "${Yellow}🔋 Battery Mode Monitoring:${Reset}"
Write-Host "   Entity: input_text.battery_management_mode"
Write-Host "   Monitor: Every 5 minutes"
Write-Host "   Modes: EMS, Self-Use, Time-of-Use, Backup, etc."
Write-Host ""

Write-Host "${Blue}💡 Quick Actions:${Reset}"
Write-Host "   Publish:    ${Yellow}.\publish.ps1${Reset}"
Write-Host "   Codegen:    ${Yellow}dotnet tool run nd-codegen${Reset}"  
Write-Host "   Build:      ${Yellow}dotnet build${Reset}"
Write-Host "   Test Run:   ${Yellow}dotnet run${Reset}"
Write-Host ""

Write-Host "${Blue}========================================${Reset}"
