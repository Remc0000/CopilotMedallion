# Copilot Medallion

Automated **Bronze → Silver → Gold + semantic model + Power BI report + Data Agent** builder for Microsoft Fabric.

You pick a source (Lakehouse / Warehouse / KQL DB / SQL DB / Mirrored DB / schema / individual tables), the app analyses the actual schemas, an LLM proposes a per-section editable spec, you tweak it if you want, click **💾 Save Specs & Build**, and four PySpark notebooks (bronze / silver / gold / reporting) get generated, deployed, executed, and auto-fixed up to N iterations if Spark fails. On success you get curated Delta tables, a Direct Lake semantic model, a Power BI report and an AISkill data agent — all in the same workspace as the workload item.

**Live demo (Remc0000 tenant):** <https://copilot.roesli.org>
**Workload item:** appears under **+ New item → Copilot Medallion** in workspaces where the workload is registered.

![Architecture](https://img.shields.io/badge/Microsoft%20Fabric-workload-blue) ![ASP.NET%20Core%208](https://img.shields.io/badge/.NET-8-purple) ![React%2018](https://img.shields.io/badge/React-18-61dafb) ![Azure%20OpenAI](https://img.shields.io/badge/Azure%20OpenAI-gpt--5.4-green)

---

## Architecture

```
┌─ Fabric portal (app.fabric.microsoft.com) ───────────────────────────┐
│  + New item → Copilot Medallion → opens editor                       │
│  ┌─ Workload outer iframe (your-domain/workload) ───────────────┐    │
│  │   Fabric Workload SDK · acquires tokens · picker dialogs     │    │
│  │  ┌─ Inner iframe (your-domain/) ───────────────────────────┐ │    │
│  │  │  React app — model picker, source picker, spec editor,  │ │    │
│  │  │  build timeline, token counter, auto-fix loop           │ │    │
│  │  └─────────────────────────────────────────────────────────┘ │    │
│  └──────────────────────────────────────────────────────────────┘    │
└──────────────────────────────────────────────────────────────────────┘
                                  │
                                  │ REST /api/*
                                  ▼
┌─ Azure App Service ──────────────────────────────────────────────────┐
│  ASP.NET Core 8 minimal API · SQLite run store · serves SPA + outer  │
│  Calls: Fabric REST, OneLake DFS, Azure OpenAI                       │
└──────────────────────────────────────────────────────────────────────┘
```

Both iframes are served from your App Service. The outer is the Fabric workload bundle (extensibility toolkit, pre-built), the inner is the React SPA (this repo's `src/frontend`).

---

## Deploying to your own tenant

If you want this in **your own Entra tenant + Fabric capacity**, here's the full setup. Plan ~45 minutes the first time.

### Prerequisites

| | |
|---|---|
| **Subscription** | Azure subscription with permission to create App Service plans + App Registrations |
| **Tenant** | Microsoft Entra tenant, admin consent to register apps |
| **Fabric** | A Fabric capacity (F2 minimum for production; trial capacity also works) |
| **Domain** *(optional)* | Public HTTPS domain you control. Without one you can use the default `*.azurewebsites.net` URL but Fabric workload registration looks nicer with a custom domain. |
| **Azure OpenAI** | A deployment of at least one chat model. The app supports `gpt-5.4`, `gpt-5-mini`, `gpt-4.1`, `gpt-4o`, `gpt-4o-mini`, `o1`, `o3-mini` (others work too — just won't get a cost estimate). |
| **GitHub** | Fork of this repo to get CI/CD to your App Service |

### Step 1 — Fork & clone

```bash
gh repo fork Remc0000/CopilotMedallion --clone
cd CopilotMedallion
```

### Step 2 — Create the Azure resources

```bash
# Pick names that work for you
RG=fabricworkload
PLAN=fabworkload-plan
APP=mycopilot-medallion      # must be globally unique
LOCATION=westeurope

az group create -g $RG -l $LOCATION
az appservice plan create -g $RG -n $PLAN --sku B2 --is-linux false
az webapp create -g $RG -n $APP -p $PLAN --runtime "DOTNET:8"
az webapp config set -g $RG -n $APP --use-32bit-worker-process false --http20-enabled true --always-on true
az webapp config appsettings set -g $RG -n $APP --settings SCM_DO_BUILD_DURING_DEPLOYMENT=false
az resource update -g $RG --name scm --namespace Microsoft.Web --resource-type basicPublishingCredentialsPolicies --parent sites/$APP --set properties.allow=true
```

> **B2 is the recommended floor**. B1 (1.75 GB RAM) works for testing but tends to OOM during 32k-token LLM responses. P0V3+ for production traffic.

### Step 3 — Register the Entra app

Two scenarios:

**(a) Single-tenant** — Copilot Medallion only ever runs in your tenant:

```bash
APP_OBJECT=$(az ad app create \
  --display-name "Copilot Medallion" \
  --sign-in-audience AzureADMyOrg \
  --web-redirect-uris "https://$APP.azurewebsites.net" "https://copilot.yourdomain.com" \
  --query appId -o tsv)
echo "AzureAd:ClientId = $APP_OBJECT"
```

**(b) Multi-tenant** — you want anyone in any Entra tenant to use your hosted instance: use `--sign-in-audience AzureADMultipleOrgs` instead. Multi-tenant requires admin consent in each consumer tenant the first time a user signs in.

Required **API permissions** (delegated):

- `Microsoft Fabric / Workspace.ReadWrite.All` — workspace/item CRUD
- `Microsoft Fabric / Item.ReadWrite.All` — lakehouse/notebook/etc.
- `Microsoft Fabric / Capacity.ReadWrite.All` *(optional, only if you let the user pick capacities)*
- `Azure Storage / user_impersonation` — OneLake DFS access (`https://storage.azure.com/user_impersonation`)
- `Microsoft Graph / User.Read` — sign-in basics

Add them in the Entra portal under **App registrations → API permissions → Add a permission**, then click **Grant admin consent** as a tenant admin.

Add **Authentication** redirect URIs (SPA platform):
- `https://<your-app>.azurewebsites.net`
- `https://copilot.yourdomain.com` *(if using a custom domain)*
- `http://localhost:5173` *(if you want to dev locally)*

### Step 4 — Deploy an Azure OpenAI resource & model

```bash
AOAI=mycopilot-aoai
az cognitiveservices account create -g $RG -n $AOAI --kind OpenAI --sku S0 -l $LOCATION
# Deploy at least one model. gpt-5.4 is preferred but gpt-4.1 is cheaper.
az cognitiveservices account deployment create -g $RG -n $AOAI \
  --deployment-name gpt-5.4 --model-name gpt-5.4 --model-version "2025-08-01" \
  --model-format OpenAI --sku-capacity 100 --sku-name "Standard"
```

Give the **App Service's system-assigned managed identity** the `Cognitive Services OpenAI User` role on the AOAI resource — the backend authenticates to AOAI via `DefaultAzureCredential` (no API key in config).

```bash
az webapp identity assign -g $RG -n $APP
PRINCIPAL=$(az webapp identity show -g $RG -n $APP --query principalId -o tsv)
AOAI_ID=$(az cognitiveservices account show -g $RG -n $AOAI --query id -o tsv)
az role assignment create --role "Cognitive Services OpenAI User" --assignee $PRINCIPAL --scope $AOAI_ID
```

### Step 5 — Configure App Service settings

```bash
az webapp config appsettings set -g $RG -n $APP --settings \
  AzureAd__TenantId="<your-tenant-id-guid>" \
  AzureAd__ClientId="$APP_OBJECT" \
  Fabric__WorkspaceId="" \
  Fabric__Scope="https://api.fabric.microsoft.com/.default" \
  OpenAI__Endpoint="https://$AOAI.openai.azure.com" \
  OpenAI__Deployment="gpt-5.4" \
  OpenAI__Models="gpt-5.4,gpt-5-mini,gpt-4.1,gpt-4o-mini" \
  OpenAI__ApiVersion="2025-04-01-preview"
```

> `Fabric__WorkspaceId` is intentionally **empty** — the workspace is resolved per-request from the Fabric workload context. Hard-coding it pins the app to one workspace.

If you want GitHub spec history (the optional "blob URL" you see in the build status):

```bash
az webapp config appsettings set -g $RG -n $APP --settings \
  GitHub__Owner="YourGitHubLogin" \
  GitHub__RunsRepo="CopilotMedallion-runs" \
  GitHub__AppRepo="CopilotMedallion" \
  GITHUB_PAT="<a PAT with repo write on the runs repo>"
```

Create an empty public/private repo `<YourGitHubLogin>/CopilotMedallion-runs` first. Leave these unset to skip GitHub entirely — specs always live in the target lakehouse `Files/spec.md` regardless.

### Step 6 — Wire up CI/CD

In your forked repo, set the GitHub Actions secret `AZURE_WEBAPP_PUBLISH_PROFILE`:

```bash
az webapp deployment list-publishing-profiles -g $RG -n $APP --xml > pp.xml
gh secret set AZURE_WEBAPP_PUBLISH_PROFILE --repo <you>/CopilotMedallion --body "$(cat pp.xml)"
rm pp.xml
```

Edit `.github/workflows/deploy.yml`:
```yaml
env:
  WEBAPP_NAME: mycopilot-medallion   # ← your $APP name
```

Push. The workflow builds the frontend, publishes the backend, and deploys to your App Service in ~3 minutes.

### Step 7 — Register the workload in your Fabric tenant

This is the magic that makes **+ New item → Copilot Medallion** appear in Fabric. The pre-built workload bundle is already in `src/backend/CopilotMedallion.Api/wwwroot/workload/` and gets served at `https://<your-app>/workload`.

1. Open **Fabric admin portal → Workload management → Workloads** (`https://app.fabric.microsoft.com/admin-portal/workloads`).
2. Click **Add workload**.
3. Upload the workload manifest from `src/backend/CopilotMedallion.Api/wwwroot/workload/manifest.json` (rename `productIcon` / `displayName` first if you want your own branding).
4. Set **Frontend URL** = `https://<your-app>.azurewebsites.net/workload`.
5. Set **Backend URL** = `https://<your-app>.azurewebsites.net/api`.
6. Grant the workload to **specific capacities** or **the whole tenant**.

After save: any workspace assigned to a granted capacity will show **Copilot Medallion** in the **+ New item** gallery.

> **Note on item icons**: replace `src/backend/CopilotMedallion.Api/wwwroot/workload/assets/fabric-icon.png` with your own 256×256 PNG (white rounded card on transparent bg works best — see how this repo's icon is built in [`infra/README.md`](infra/README.md)).

### Step 8 — Custom domain (optional)

```bash
# Verify domain ownership in App Service first (TXT record), then:
az webapp config hostname add -g $RG --webapp-name $APP --hostname copilot.yourdomain.com
az webapp config ssl create -g $RG --name $APP --hostname copilot.yourdomain.com
az webapp config ssl bind -g $RG --name $APP --certificate-thumbprint <thumb> --ssl-type SNI
```

Then **add the custom URL as a redirect URI** in the Entra app registration (step 3) and re-grant admin consent.

### Step 9 — Smoke test

1. Open `https://<your-app>.azurewebsites.net/`.
2. Sign in with an account that has access to a Fabric workspace.
3. Pick a model, pick a source lakehouse + tables, click **Save Specs & Build**.
4. Watch the build timeline; expect 5-15 min for the first run.

Then test from Fabric:

1. Open a workspace assigned to a capacity where you granted the workload.
2. **+ New item → Copilot Medallion** → name it → **Create**.
3. The editor opens; pick a source via the top-bar picker.

---

## Configuration reference

App Service settings (`__` underscore-pairs map to `:` colons in `appsettings.json`):

| Key | Required | Notes |
|---|---|---|
| `AzureAd__TenantId` | yes | Your Entra tenant GUID (or `common` for multi-tenant) |
| `AzureAd__ClientId` | yes | App registration's Application (client) ID |
| `Fabric__Scope` | yes | `https://api.fabric.microsoft.com/.default` |
| `Fabric__WorkspaceId` | no | Leave empty — resolved per-request from Fabric context |
| `OpenAI__Endpoint` | yes | `https://<aoai>.openai.azure.com` |
| `OpenAI__Deployment` | yes | Default model deployment name |
| `OpenAI__Models` | no | Comma-list of additional deployments to show in the picker |
| `OpenAI__ApiVersion` | no | Defaults to `2025-04-01-preview` |
| `GitHub__Owner` / `__RunsRepo` / `__AppRepo` | no | Optional spec history |
| `GITHUB_PAT` | no | PAT for the runs repo; only used if all 3 GitHub keys above are set |
| `RUN_DB_PATH` | no | SQLite path; defaults to `$HOME/data/runs.db` |

---

## Local development

```bash
# Frontend (Vite dev server, port 5173, proxies /api to localhost:5000)
cd src/frontend
npm install
npm run dev

# Backend (another shell)
cd src/backend/CopilotMedallion.Api
dotnet user-secrets set "AzureAd:TenantId" "<your-tenant>"
dotnet user-secrets set "AzureAd:ClientId" "<your-client>"
dotnet user-secrets set "OpenAI:Endpoint" "https://<aoai>.openai.azure.com"
dotnet user-secrets set "OpenAI:Deployment" "gpt-5.4"
az login    # so DefaultAzureCredential can pick up your identity for AOAI
dotnet run
```

Open <http://localhost:5173> — sign in with your tenant account.

---

## Repo layout

```
src/backend/CopilotMedallion.Api/   ASP.NET Core 8 minimal API
  Endpoints/                        /api/* route definitions
  Services/                         Fabric REST, Azure OpenAI, SQLite run store
  wwwroot/                          Hosts SPA + Fabric workload outer bundle
src/frontend/                       React + Vite + Fluent UI + MSAL
specs/                              template.md (LLM fallback)
.github/workflows/                  deploy.yml
infra/                              Setup notes
```

---

## How the build flow works

1. **Spec proposal** — `/api/specs/preview` reads the actual Delta schemas of the chosen tables (via OneLake DFS) and asks the LLM to propose a 7-section spec (Generic / Bronze / Silver / Gold / Semantic / Report / Data Agent).
2. **Save** — `/api/specs` writes the spec to the target lakehouse `Files/spec.md` and persists in SQLite. Optionally pushes to GitHub.
3. **Build** — `/api/build` creates (or reuses) the target lakehouse, then asks the LLM to generate Python cells for all 4 notebooks in one call.
4. **Deploy + run** — for each layer, the notebook is updated (or created) and submitted as a Spark job. The backend polls the job until terminal.
5. **Auto-fix** — on Spark failure, the frontend feeds the traceback back to `/api/runs/{id}/fix-spec` to revise the spec, then re-runs. Up to N iterations (default 5).
6. **Reporting** — the last notebook creates a Direct Lake semantic model on Gold, a Power BI report, and a Data Agent (AISkill).

All artifacts live in the **same workspace as the Copilot Medallion item** — no cross-workspace creation.

---

## Cost expectations

Per build (gpt-5.4, ~13 source tables):

- Spec proposal: ~5K input + ~5K output tokens
- Notebook generation: ~8K + ~25K output tokens
- Auto-fix iteration (when needed): ~5K + ~8K tokens each
- **Per successful build**: roughly **$0.30 – $0.80** in Azure OpenAI charges
- Fabric Spark CU consumption: variable, but typically 3-6 vCore-minutes per layer

Switch the model to `gpt-4o-mini` or `gpt-4.1` in the section 1 dropdown to cut costs ~10x with some quality trade-off.

---

## License

MIT. The pre-built Fabric workload SDK bundle in `wwwroot/workload/` is from the [Microsoft Fabric Workload Development Kit samples](https://learn.microsoft.com/fabric/workload-development-kit/) and retains its own license headers.

---

## Acknowledgements

- [Microsoft Fabric Workload Development Kit](https://learn.microsoft.com/fabric/workload-development-kit/development-kit-overview)
- [skills-for-fabric](https://github.com/microsoft/skills-for-fabric) — FabricDataEngineer agent + e2e-medallion-architecture skill
- [powerbi-agentic-plugins](https://github.com/RuiRomano/powerbi-agentic-plugins) — semantic-model-authoring + report-authoring skills