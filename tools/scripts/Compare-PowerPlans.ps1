# Compares hidden+visible settings of the OEM power plans against Windows Balanced.
param(
    [hashtable]$Plans = [ordered]@{
        Balanced = '381b4222-f694-41f0-9685-ff5bb260df2e'
        Office   = '5da85d85-eb0c-4710-a805-6da8bbcf1f02'
        Gaming   = '653fbb7f-248b-41d0-aa49-03fabbfd5e8e'
        High     = 'a9932169-f1fb-4dff-9034-fe1c4ca1886e'
    }
)

function Get-PlanSettings([string]$Guid) {
    $map = @{}
    $sub = ''; $set = ''; $name = ''
    foreach ($line in (powercfg /qh $Guid)) {
        # Language-agnostic: rely on indentation and the AC/DC tokens, not localized labels.
        if ($line -match '^  \S.*:\s+([0-9a-f-]{36})\s+\((.*)\)') { $sub = $Matches[2] }
        elseif ($line -match '^    \S.*:\s+([0-9a-f-]{36})\s+\((.*)\)') { $set = $Matches[1]; $name = "$sub / $($Matches[2])" }
        elseif ($line -match '\bAC\b.*:\s+(0x[0-9a-fA-F]+)\s*$') { $map["$set|$name|AC"] = [Convert]::ToInt64($Matches[1], 16) }
        elseif ($line -match '\bDC\b.*:\s+(0x[0-9a-fA-F]+)\s*$') { $map["$set|$name|DC"] = [Convert]::ToInt64($Matches[1], 16) }
    }
    $map
}

$data = [ordered]@{}
foreach ($k in $Plans.Keys) { $data[$k] = Get-PlanSettings $Plans[$k] }
$keys = $data.Values | ForEach-Object { $_.Keys } | Sort-Object -Unique
foreach ($key in $keys) {
    $vals = foreach ($k in $Plans.Keys) { $data[$k][$key] }
    if (($vals | Sort-Object -Unique).Count -gt 1) {
        $parts = $key.Split('|')
        $row = [ordered]@{ Setting = $parts[1]; Mode = $parts[2]; Guid = $parts[0] }
        foreach ($k in $Plans.Keys) { $row[$k] = $data[$k][$key] }
        [pscustomobject]$row
    }
}
