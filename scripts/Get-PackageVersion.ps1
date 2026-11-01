[CmdletBinding()]
param(
    [datetime]$Timestamp = (Get-Date)
)

$Timestamp.ToString("yyyy.MM.dd.HHmm", [Globalization.CultureInfo]::InvariantCulture)
