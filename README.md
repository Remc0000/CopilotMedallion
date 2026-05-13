# Copilot Medallion

One-click automation of the "Bob the Builder" Fabric medallion demo (`Remc0000/Fabric/prompt.md`).

Live at: <https://copilot.roesli.org>

## What it does
1. User signs in with their Entra account.
2. Picks a source Lakehouse + tables from the `CopilotWorkload` workspace.
3. App generates a run-specific spec (markdown derived from `prompt.md`) and pushes it to a new branch in `Remc0000/CopilotMedallion-runs`.
4. User clicks **Continue**.
5. App creates a target Lakehouse, deploys the orchestrator notebook, and runs it to build Bronze/Silver/Gold + (where applicable) a Power BI report.
6. Run status polled on the page; links to created artifacts shown when done.

## Repo layout
```
src/backend/        ASP.NET Core 8 minimal API + static-hosting for SPA
src/frontend/       React + Vite + Fluent UI + MSAL
notebooks/          medallion_builder.ipynb (parameterised orchestrator)
specs/              template.md (run-spec template)
.github/workflows/  deploy.yml (CI/CD to Azure App Service)
infra/              Azure setup notes
```

## Local dev
```
# Frontend
cd src/frontend && npm install && npm run dev
# Backend (in another shell)
cd src/backend/CopilotMedallion.Api && dotnet run
```

## Deployment
Pushes to `main` build & deploy to App Service `copilot-roesli` (RG `fabricworkload`).
