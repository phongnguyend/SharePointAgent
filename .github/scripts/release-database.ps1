. "$PSScriptRoot/deployment-helpers.ps1"
Open-DatabaseFirewall
Invoke-Database -InputFile "$env:RUNNER_TEMP/migrations.sql"
