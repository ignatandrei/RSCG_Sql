Now about generating and verifying the package

-------

read @final_instructions.md . Do not do anything !

please add to the Roslyn .csproj project everything it is needed to have info for a nuget package( you are in a github repo) 

please add also a github action to build and test and pack the project


Now I want the nuget package to have the version implemented as the current date down to minutes. Make a powershell for this. Implement in CI

------

As you see , pretty simple. And yes, he has generated a powershell and put the code in CI

```powershell

[CmdletBinding()]
param(
    [datetime]$Timestamp = (Get-Date)
)

$Timestamp.ToString("yyyy.MM.dd.HHmm", [Globalization.CultureInfo]::InvariantCulture)

```
and put in CI

```yaml

- name: Calculate package version
  id: package-version
  shell: pwsh
  run: |
    $version = ./scripts/Get-PackageVersion.ps1
    "version=$version" >> $env:GITHUB_OUTPUT

- name: Pack generator
  run: dotnet pack RSCG_Sql.Generator/RSCG_Sql.Generator.csproj --configuration Release --no-build --output artifacts/packages -p:Version=${{ steps.package-version.outputs.version }}

  ```
