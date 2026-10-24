# Drives a running QueueJanne window through Windows UI Automation, for testing the real app on the demo namespace.
# It never loads saved connections: the app starts with a throwaway QUEUEJANNE_HOME.
#
#   Import-Module ./tools/UiDriver/QueueJanne.UiDriver.psm1 -Force
#   Start-QJ -Demo            # builds Release, starts the app, opens the demo namespace
#   Select-QJ 'orders'        # selects a tree item or list row by name
#   Invoke-QJ 'Refresh'       # presses a button, menu item or toggle by name
#   Save-QJShot shot.png      # captures the window, also when it's behind other windows
#   Get-QJLag                 # UI freezes over 50 ms since the last call
#   Stop-QJ
Set-StrictMode -Version Latest
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms
if (-not ('QJNative' -as [type])) {
    Add-Type @'
using System; using System.Runtime.InteropServices;
public static class QJNative {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
'@
}
[QJNative]::SetProcessDPIAware() | Out-Null

$script:A = [System.Windows.Automation.AutomationElement]
$script:Scope = [System.Windows.Automation.TreeScope]
$script:Process = $null
$script:QjHome = $null
$script:LagLog = $null
$script:LagLinesRead = 0
$repo = Split-Path (Split-Path $PSScriptRoot)

# Every PowerShell call is a new process, so the running instance is remembered in a file and picked up on import.
$script:SessionFile = Join-Path $env:TEMP 'queuejanne-uidriver.json'
if (Test-Path $script:SessionFile) {
    $saved = Get-Content $script:SessionFile -Raw | ConvertFrom-Json
    $script:Process = Get-Process -Id $saved.ProcessId -ErrorAction SilentlyContinue
    if ($script:Process) { $script:QjHome = $saved.Home; $script:LagLog = $saved.LagLog; $script:LagLinesRead = $saved.LagLinesRead }
}

function Save-QJSession {
    @{ ProcessId = $script:Process.Id; Home = $script:QjHome; LagLog = $script:LagLog; LagLinesRead = $script:LagLinesRead } |
        ConvertTo-Json | Set-Content $script:SessionFile
}

function Start-QJ {
    param([switch]$Demo, [switch]$NoBuild, [string]$Exe = "$repo/src/QueueJanne.App/bin/Release/net10.0/QueueJanne.exe")
    Stop-QJ
    $env:DOTNET_ROOT = if ($env:DOTNET_ROOT) { $env:DOTNET_ROOT } else { "$env:LOCALAPPDATA\Microsoft\dotnet" }
    $env:PATH = "$env:DOTNET_ROOT;$env:PATH"
    if (-not $NoBuild -and -not $PSBoundParameters.ContainsKey("Exe")) {
        $output = dotnet build "$repo/src/QueueJanne.App" -c Release -v q -nologo 2>&1
        if ($LASTEXITCODE -ne 0) { $output | Select-Object -Last 20; throw 'Build failed.' }
    }

    $script:QjHome = Join-Path $env:TEMP ("qj-ui-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory $script:QjHome | Out-Null
    $script:LagLog = Join-Path $script:QjHome 'lag.log'
    $script:LagLinesRead = 0
    $env:QUEUEJANNE_HOME = $script:QjHome
    $env:QUEUEJANNE_DIAGNOSTICS = $script:LagLog
    $started = [Diagnostics.Stopwatch]::StartNew()
    $script:Process = Start-Process $Exe -PassThru
    Remove-Item Env:QUEUEJANNE_HOME, Env:QUEUEJANNE_DIAGNOSTICS
    Save-QJSession
    Wait-QJ { Get-QJWindow } -TimeoutSeconds 20 | Out-Null
    Write-Host "Window up in $($started.ElapsedMilliseconds) ms"
    if ($Demo) {
        Invoke-QJ 'Open the demo namespace'
        Wait-QJ { Find-QJ 'orders' -Type TreeItem } | Out-Null
    }
}

function Stop-QJ {
    if ($script:Process -and -not $script:Process.HasExited) { $script:Process.Kill(); $script:Process.WaitForExit() }
    if ($script:LagLog -and (Test-Path $script:LagLog)) { Copy-Item $script:LagLog (Join-Path $env:TEMP 'queuejanne-last-lag.log') -Force }
    if ($script:QjHome -and (Test-Path $script:QjHome)) { Remove-Item -Recurse -Force $script:QjHome -ErrorAction SilentlyContinue }
    $script:Process = $null
    Remove-Item $script:SessionFile -ErrorAction SilentlyContinue
}

function Get-QJWindow {
    if (-not $script:Process) { throw 'Start-QJ first.' }
    $condition = New-Object System.Windows.Automation.PropertyCondition($A::ProcessIdProperty, $script:Process.Id)
    $A::RootElement.FindFirst($Scope::Children, $condition)
}

function Get-QJWindows {
    $condition = New-Object System.Windows.Automation.PropertyCondition($A::ProcessIdProperty, $script:Process.Id)
    @($A::RootElement.FindAll($Scope::Children, $condition))
}

# Dialogs show up in UI Automation as Window elements inside the main window.
function Get-QJDialog {
    param([string]$Name)
    $dialogs = Find-QJ -Type Window -All
    if ($Name) { $dialogs = $dialogs | Where-Object { $_.Current.Name -eq $Name } }
    @($dialogs) | Select-Object -First 1
}

function Wait-QJ {
    param([scriptblock]$Until, [int]$TimeoutSeconds = 10)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $result = & $Until
        if ($result) { return $result }
        Start-Sleep -Milliseconds 25
    }
    throw "Timed out waiting for: $Until"
}

function Find-QJ {
    param([string]$Name, [string]$Type, [switch]$All, [switch]$Like, $Root)
    $roots = if ($Root) { @($Root) } else { Get-QJWindows }
    $conditions = @()
    if ($Name -and -not $Like) { $conditions += New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, $Name) }
    if ($Type) {
        $controlType = [System.Windows.Automation.ControlType]::$Type
        $conditions += New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, $controlType)
    }
    $condition = switch ($conditions.Count) {
        0 { [System.Windows.Automation.Condition]::TrueCondition }
        1 { $conditions[0] }
        default { New-Object System.Windows.Automation.AndCondition([System.Windows.Automation.Condition[]]$conditions) }
    }
    $found = foreach ($r in $roots) { $r.FindAll($Scope::Descendants, $condition) }
    if ($Like) { $found = $found | Where-Object { $_.Current.Name -like $Name } }
    if ($All) { return @($found) }
    @($found) | Select-Object -First 1
}

function Resolve-QJ($Target, [string]$Type) {
    if ($Target -is [System.Windows.Automation.AutomationElement]) { return $Target }
    $element = Find-QJ $Target -Type $Type
    if (-not $element) { throw "No element named '$Target'." }
    $element
}

function Invoke-QJ {
    param($Target, [string]$Type)
    $element = Resolve-QJ $Target $Type
    foreach ($pattern in 'InvokePattern', 'TogglePattern', 'SelectionItemPattern', 'ExpandCollapsePattern') {
        $patternType = "System.Windows.Automation.$pattern" -as [type]
        $object = $null
        if ($element.TryGetCurrentPattern($patternType::Pattern, [ref]$object)) {
            switch ($pattern) {
                'InvokePattern' { $object.Invoke() }
                'TogglePattern' { $object.Toggle() }
                'SelectionItemPattern' { $object.Select() }
                'ExpandCollapsePattern' { if ($object.Current.ExpandCollapseState -eq 'Collapsed') { $object.Expand() } else { $object.Collapse() } }
            }
            return
        }
    }
    throw "'$($element.Current.Name)' can't be invoked."
}

function Select-QJ {
    param($Target, [string]$Type)
    $element = Resolve-QJ $Target $Type
    $element.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
}

function Expand-QJ {
    param($Target)
    $element = Resolve-QJ $Target 'TreeItem'
    # Avalonia's tree items don't offer ExpandCollapsePattern; their expander is the first child button.
    $isExpanded = @($element.FindAll($Scope::Children, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::TreeItem)))).Count -gt 0
    if (-not $isExpanded) {
        $expander = $element.FindFirst($Scope::Children, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)))
        Invoke-QJ $expander
    }
}

function Set-QJText {
    param($Target, [string]$Text)
    $element = Resolve-QJ $Target 'Edit'
    $element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($Text)
}

# Real mouse click in the middle of an element. Moves the user's pointer, so prefer Invoke-QJ or Select-QJ.
function Click-QJ {
    param($Target, [switch]$Double, [switch]$Right)
    $element = Resolve-QJ $Target
    Show-QJ
    $r = $element.Current.BoundingRectangle
    [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2))
    $signature = '[DllImport("user32.dll")] public static extern void mouse_event(int f, int x, int y, int d, int e);'
    if (-not ('QJMouse' -as [type])) { Add-Type -Name QJMouse -MemberDefinition $signature -Namespace '' }
    $down, $up = if ($Right) { 8, 16 } else { 2, 4 }
    $times = if ($Double) { 2 } else { 1 }
    1..$times | ForEach-Object { [QJMouse]::mouse_event($down, 0, 0, 0, 0); [QJMouse]::mouse_event($up, 0, 0, 0, 0) }
}

function Show-QJ {
    param($Window)
    $Window = if ($Window) { $Window } elseif (Get-QJDialog) { Get-QJDialog } else { Get-QJWindow }
    [QJNative]::SetForegroundWindow([IntPtr]$Window.Current.NativeWindowHandle) | Out-Null
    Start-Sleep -Milliseconds 150
}

# Keys in SendKeys syntax, for example '^d' (Ctrl+D), '{F5}', '{DELETE}'. Brings the open dialog, or else the main window,
# to the front first.
function Send-QJKeys {
    param([string]$Keys, $Window)
    Show-QJ $Window
    [System.Windows.Forms.SendKeys]::SendWait($Keys)
}

function Save-QJShot {
    param([string]$Path, $Window)
    $Window = if ($Window) { $Window } else { Get-QJWindow }
    $handle = [IntPtr]$Window.Current.NativeWindowHandle
    $rect = New-Object QJNative+RECT
    [QJNative]::GetWindowRect($handle, [ref]$rect) | Out-Null
    $bitmap = New-Object System.Drawing.Bitmap(($rect.Right - $rect.Left), ($rect.Bottom - $rect.Top))
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $dc = $graphics.GetHdc()
    [QJNative]::PrintWindow($handle, $dc, 2) | Out-Null  # PW_RENDERFULLCONTENT
    $graphics.ReleaseHdc($dc)
    $Path = [IO.Path]::GetFullPath($Path)
    $bitmap.Save($Path)
    $graphics.Dispose(); $bitmap.Dispose()
    $Path
}

function Get-QJText {
    param([string]$Like = '*')
    Find-QJ $Like -Type Text -All -Like | ForEach-Object { $_.Current.Name } | Where-Object { $_ }
}

function Get-QJLag {
    if (-not (Test-Path $script:LagLog)) { return @() }
    $lines = @(Get-Content $script:LagLog | Where-Object { $_ -notlike '#*' })
    $new = $lines | Select-Object -Skip $script:LagLinesRead
    $script:LagLinesRead = $lines.Count
    Save-QJSession
    @($new)
}

# Runs an action and reports how long until the condition holds, and the UI freezes in between.
function Measure-QJ {
    param([string]$Label, [scriptblock]$Action, [scriptblock]$Until, [int]$TimeoutSeconds = 60)
    Get-QJLag | Out-Null
    $watch = [Diagnostics.Stopwatch]::StartNew()
    & $Action
    Wait-QJ $Until -TimeoutSeconds $TimeoutSeconds | Out-Null
    $elapsed = $watch.ElapsedMilliseconds
    Start-Sleep -Milliseconds 300
    $lags = @(Get-QJLag | ForEach-Object { [int]($_ -split ' ')[1] })
    $worst = if ($lags.Count) { ($lags | Measure-Object -Maximum).Maximum } else { 0 }
    [pscustomobject]@{ Step = $Label; Ms = $elapsed; Freezes = $lags.Count; WorstFreezeMs = $worst }
}

Export-ModuleMember -Function *-QJ*
