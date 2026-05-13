# Infrastructure notes

All resources live in subscription `2374d80b-4c7e-4800-9bfb-06af863af84a`, RG `fabricworkload`, region West US 3.

| Resource | Name | Notes |
| --- | --- | --- |
| App Service Plan | `fabworkload-plan` | B1 Windows; shared with `fabworkload-ontology`. |
| Web App | `copilot-roesli` | .NET 8 in-process, system-assigned MI, HTTPS only. |
| Custom domain | `copilot.roesli.org` | App Service managed cert (SNI). DNS in same RG (`roesli.org` zone). |
| Entra App | `Copilot Medallion (roesli.org)` | Multi-tenant. App ID `d06ba5f1-0508-4796-b221-b9d4b21b2201`. Web + SPA redirects for prod, azurewebsites.net, localhost:5173. Admin-consented delegated permissions on `00000009-...` (PowerBI/Fabric API): Workspace.ReadWrite.All, Item.ReadWrite.All, Lakehouse.ReadWrite.All, Notebook.ReadWrite.All, Capacity.Read.All, Dataset.ReadWrite.All, Report.ReadWrite.All; plus Graph User.Read. |
| Fabric workspace | `CopilotWorkload` | `ca85ec47-ed39-4b52-be27-50aa56979216` on capacity `Trial-Remco` (FTL64). |

## One-time post-provision steps still needed
1. Create a GitHub fine-grained PAT scoped to `Remc0000/CopilotMedallion-runs` with `Contents: Read & write`. Set as App Service app setting `GITHUB_PAT` (currently `__SET_ME__`).
2. Download App Service publish profile, save as GitHub repo secret `AZURE_WEBAPP_PUBLISH_PROFILE` to enable CI/CD via `.github/workflows/deploy.yml`.
   `az webapp deployment list-publishing-profiles -g fabricworkload -n copilot-roesli --xml | clip`

## Manual redeploy (when not using GH Actions)
```pwsh
cd src/frontend && npm run build
cd ../backend/CopilotMedallion.Api && dotnet publish -c Release -o ../publish
cd ..
Compress-Archive -Path publish/* -DestinationPath app.zip -Force
az webapp deploy -g fabricworkload -n copilot-roesli --src-path app.zip --type zip
```
