# -----------------------------------------------------------------------------
# NetDaemonApps Publish Script
# This script builds and publishes the NetDaemon apps to Home Assistant
# -----------------------------------------------------------------------------

param(
    [switch]$SkipCodegen,
    [switch]$SkipBuild,
    [switch]$Force
)

# Set error handling
$ErrorActionPreference = "Stop"

# Colors for output
$Green = "`e[32m"
$Yellow = "`e[33m"
$Red = "`e[31m"
$Blue = "`e[36m"
$Reset = "`e[0m"

Write-Host "${Blue}========================================${Reset}"
Write-Host "${Blue}  NetDaemonApps Publish Script${Reset}"
Write-Host "${Blue}========================================${Reset}"
Write-Host ""

try {
    # Get the script directory (project root)
    $ProjectRoot = $PSScriptRoot
    Set-Location $ProjectRoot
    
    Write-Host "${Blue}Project Directory:${Reset} $ProjectRoot"
    Write-Host ""

    # Step 1: Generate Home Assistant types (unless skipped)
    if (-not $SkipCodegen) {
        Write-Host "${Yellow}🔄 Generating Home Assistant types...${Reset}"
        
        # Check if nd-codegen tool is available
        $toolCheck = dotnet tool list | Select-String "netdaemon.hassclient.codegen"
        if (-not $toolCheck) {
            Write-Host "${Yellow}⚠️  Installing NetDaemon codegen tool...${Reset}"
            dotnet tool install NetDaemon.HassClient.CodeGen
        }
        
        dotnet tool run nd-codegen
        if ($LASTEXITCODE -ne 0) {
            throw "Code generation failed with exit code $LASTEXITCODE"
        }
        Write-Host "${Green}✅ Code generation completed${Reset}"
        Write-Host ""
    } else {
        Write-Host "${Yellow}⏭️  Skipping code generation${Reset}"
        Write-Host ""
    }

    # Step 2: Build the project (unless skipped)
    if (-not $SkipBuild) {
        Write-Host "${Yellow}🔨 Building project...${Reset}"
        
        dotnet build NetDaemonApps.csproj --configuration Release
        if ($LASTEXITCODE -ne 0) {
            throw "Build failed with exit code $LASTEXITCODE"
        }
        Write-Host "${Green}✅ Build completed${Reset}"
        Write-Host ""
    } else {
        Write-Host "${Yellow}⏭️  Skipping build${Reset}"
        Write-Host ""
    }

    # Step 3: Publish to NetDaemon5
    Write-Host "${Yellow}📦 Publishing to NetDaemon5...${Reset}"
    
    # Check if target directory is accessible
    $publishDir = "\\192.168.1.135\config\netdaemon5"
    if (-not (Test-Path $publishDir -ErrorAction SilentlyContinue)) {
        Write-Host "${Red}❌ Cannot access publish directory: $publishDir${Reset}"
        Write-Host "${Yellow}💡 Make sure Home Assistant is running and the network share is accessible${Reset}"
        throw "Publish directory not accessible"
    }
    
    dotnet publish NetDaemonApps.csproj /p:PublishProfile=NetDaemon5Profile
    if ($LASTEXITCODE -ne 0) {
        throw "Publish failed with exit code $LASTEXITCODE"
    }
    
    Write-Host "${Green}✅ Published successfully to: $publishDir${Reset}"
    Write-Host ""

    # Step 4: Success message and next steps
    Write-Host "${Green}🎉 Publish completed successfully!${Reset}"
    Write-Host ""
    Write-Host "${Blue}Next steps:${Reset}"
    Write-Host "1. ${Yellow}Restart NetDaemon5 add-on${Reset} in Home Assistant"
    Write-Host "2. ${Yellow}Check logs${Reset} for any startup errors"
    Write-Host "3. ${Yellow}Verify${Reset} that battery_management_mode entity is updating"
    Write-Host ""
    Write-Host "${Blue}Published features:${Reset}"
    Write-Host "• 🔋 Battery mode monitoring (every 5 minutes)"
    Write-Host "• ⚡ Energy management automations"
    Write-Host "• 🏠 Vacation security features"
    Write-Host ""

} catch {
    Write-Host ""
    Write-Host "${Red}❌ Publish failed: $($_.Exception.Message)${Reset}"
    Write-Host ""
    Write-Host "${Yellow}💡 Troubleshooting tips:${Reset}"
    Write-Host "• Check that Home Assistant is running"
    Write-Host "• Verify network connectivity to 192.168.1.135"
    Write-Host "• Ensure NetDaemon5 add-on is installed"
    Write-Host "• Try running with -Force parameter"
    Write-Host ""
    exit 1
}

Write-Host "${Blue}========================================${Reset}"
Write-Host "${Green}✨ Ready to automate your home! ✨${Reset}"
Write-Host "${Blue}========================================${Reset}"
