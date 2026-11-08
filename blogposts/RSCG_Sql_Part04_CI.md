Now I will describe how I generated and verified the package.

-------

I asked the AI to read @final_instructions.md and do nothing else.

I asked it to add the information that the Roslyn .csproj project needs for a NuGet package. I also noted that the project is in a GitHub repository.

I also asked it to add a GitHub Action to build, test, and pack the project.


Then I asked it to set the NuGet package version to the current date and time, down to the minute. I asked it to write a PowerShell script for this and implement it in CI.

------

As you can see, this seemed simple. The AI generated a PowerShell script and added the code to CI:

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
