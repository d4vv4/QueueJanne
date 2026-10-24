# Times common steps on the demo namespace and reports UI freezes. Starts its own QueueJanne instance.
#   powershell -File tools/UiDriver/Measure-Demo.ps1 [-NoBuild]
param([switch]$NoBuild, [string]$Screenshots, [string]$Exe)
Import-Module "$PSScriptRoot/QueueJanne.UiDriver.psm1" -Force -WarningAction SilentlyContinue
if ($Exe) { Start-QJ -Demo -Exe $Exe } else { Start-QJ -Demo -NoBuild:$NoBuild }

function SubQueue([string]$Entity, [string]$Name) {
    Expand-QJ $Entity
    Wait-QJ { (Find-QJ $Entity -Type TreeItem).FindFirst('Children', (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $Name))) }
}

function Peeked { Get-QJText '*Peeked in*' }

$results = @()
$results += Measure-QJ 'Overview -> orders dead-letter' { Select-QJ (SubQueue 'orders' 'Dead-letter') } { Peeked }
$results += Measure-QJ 'notifications dead-letter (41)' { Select-QJ (SubQueue 'notifications' 'Dead-letter') } { Peeked }
Expand-QJ 'billing-events'
$results += Measure-QJ 'ledger dead-letter (1 000 of 1 204)' { Select-QJ (SubQueue 'ledger' 'Dead-letter') } { Peeked }
$results += Measure-QJ 'audit-log messages (1 000 of 15 230)' { Select-QJ (SubQueue 'audit-log' 'Messages') } { Peeked }
$results += Measure-QJ 'audit-log: all messages' {
    Invoke-QJ 'How many messages' -ErrorAction SilentlyContinue
    $combo = Find-QJ 'How many messages' -Type ComboBox
    $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    Start-Sleep -Milliseconds 200
    Select-QJ (Find-QJ 'All messages' -Type ListItem)
} { Get-QJText '15*messages' } -TimeoutSeconds 120
$results += Measure-QJ 'audit-log: filter "OrderPaid"' { Set-QJText 'Filter loaded messages' 'OrderPaid' } { Get-QJText '* shown*' }
$results += Measure-QJ 'audit-log: clear filter' { Set-QJText 'Filter loaded messages' '' } { -not (Get-QJText '* shown*') }
$results += Measure-QJ 'audit-log: group by subject' {
    $combo = Find-QJ 'Group by' -Type ComboBox
    $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    Start-Sleep -Milliseconds 200
    Select-QJ (Find-QJ 'Subject' -Type ListItem)
} { $true }
$results += Measure-QJ 'F5 refresh (counts + 15 230 rows)' { Invoke-QJ 'Refresh' } { Peeked } -TimeoutSeconds 120
if ($Screenshots) { Save-QJShot (Join-Path $Screenshots 'measure-end.png') | Out-Null }
$results += Measure-QJ 'switch to overview tab' { Select-QJ (Find-QJ 'Demo namespace · dead letters' -Type TabItem) } { $true }
$results | Format-Table -AutoSize
Stop-QJ
