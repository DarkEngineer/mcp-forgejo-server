# Forgejo MCP Server

Serwer [Model Context Protocol](https://modelcontextprotocol.io) (MCP) dla
[Forgejo](https://forgejo.org/) (kompatybilnego z Gitea) — self-hostowanego
hostingu repozytoriów git. Zbudowany w .NET / C# na bazie SDK
[`ModelContextProtocol`](https://www.nuget.org/packages/ModelContextProtocol)
(wersja 2.2.0, .NET 10).

Serwer udostępnia operacje Forgejo jako MCP **tools** (narzędzia) i
**resources** (zasoby) — dzięki temu każdy klient MCP (np. agenci w IDE)
może sterować instancją Forgejo przez proste żądania tekstowe.

Repozytorium pełni też rolę **szablonu** (template) do budowy własnych serwerów
MCP — zobacz [Jak użyć jako szablon](#jak-użyć-jako-szablon-innych-serwerów-mcp).

## Stack

| Składnik        | Wartość                            |
| --------------- | ---------------------------------- |
| Runtime         | .NET 10 (`net10.0`)                |
| SDK MCP         | `ModelContextProtocol` 2.2.0       |
| Hosting         | `Microsoft.Extensions.Hosting`     |
| Transport       | stdio (stdin/stdout, NDJSON)       |
| Wynik           | aplikacja wykonywalna (`Exe`)      |

## Struktura repozytorium

```
.
├── README.md
├── LICENSE                 # MIT
├── .gitignore
├── ForgejoMcp.sln
├── .github/workflows/ci.yml    # CI (GitHub Actions)
├── .forgejo/workflows/ci.yml   # CI (Forgejo Actions) — ten sam plik
├── scripts/
│   └── mcp_smoke.py            # test dymny protokołu MCP (stdio)
├── src/
│   ├── ForgejoClient/        # typowany klient REST API Forgejo v1 (retry, auth)
│   │   ├── ForgejoClient.cs
│   │   ├── ForgejoCredentials.cs
│   │   ├── ForgejoException.cs
│   │   ├── ForgejoJson.cs
│   │   ├── ListResult.cs
│   │   ├── Models.cs
│   │   └── RequestOptions.cs
│   └── ForgejoMcp/           # serwer MCP (tools + resources nad klientem)
│       ├── Program.cs              # host + warstwa konfiguracji
│       ├── ForgejoServerOptions.cs # bind sekcji "Forgejo" z pliku i zmiennych ENV
│       ├── ForgejoMcpToolSurface.cs
│       ├── ForgejoResources.cs
│       └── appsettings.json
└── tests/
    └── ForgejoMcp.Tests/     # xUnit: klient REST, konfiguracja, surface MCP
```

## Wymagania (prerequisites)

- **.NET 10 SDK** (`dotnet --version` → `10.0.x`) — do budowy i uruchomienia.
- **Instancja Forgejo/Gitea** z wygenerowanym tokenem dostępu (Settings →
  Applications → Generate New Token) albo parą użytkownik/hasło (Basic auth).
- **Python 3** — wyłącznie do testu dymnego `scripts/mcp_smoke.py` (opcjonalnie).
- Jeśli instancja korzysta z **wewnętrznego certyfikatu TLS (własny CA)**:
  udostępnij .NET klucz CA przed startem, np.
  `export SSL_CERT_FILE=/path/to/ca.crt` — klient NIGDY nie wyłącza
  weryfikacji certyfikatów.

## Uruchomienie

Serwer **wymaga** ustawienia `FORGEJO_URL`; reszta konfiguracji
(opis poniżej) jest nadpisywana przez zmienne środowiskowe, więc na świeżym
klonie wystarczy:

```sh
export FORGEJO_URL=https://twoja-instancja-forgejo
export FORGEJO_TOKEN=***

# opcjonalnie: dotnet build --configuration Release
dotnet run --project src/ForgejoMcp
```

Albo podłącz klienta MCP bezpośrednio do skompilowanego binara:

```
src/ForgejoMcp/bin/Release/net10.0/ForgejoMcp
```

**Uwaga dot. stdio:** stdin musi pozostać otwarty po stronie klienta MCP
— EOF na stdin powoduje natychmiastowe zamknięcie hosta (standardowy model
"stdin = lifecycle" w SDK MCP). Klienty takie jak Claude Desktop/Claude Code
robią to za Ciebie automatycznie.

### Test dymny (protokół MCP przez stdio)

Weryfikuje handshake `initialize` + listę narzędzi, nie wymaga żywej
instancji (dozwolone jest dummy URL):

```sh
export FORGEJO_URL=https://przyklad.invalid   # wartość dowolna, ważna tylko do walidacji
dotnet build ForgejoMcp.sln -c Release
python3 scripts/mcp_smoke.py
```

Przykładowa, udana odpowiedź:

```
initialize OK: forgejo-mcp-server v1.0.0 (2025-06-18)
tools (19): list_repos, create_issue, list_commits, list_issues, get_repo, get_file, list_pull_requests, get_issue, get_pull_request, get_pull_request_files, list_releases, list_file_tree, list_branches, get_branch, list_labels, create_label, update_issue, add_issue_comment, create_pull_request
resources: instance
resource templates: repo/{owner}/{name}
```

## Konfiguracja

Precedencja (najwyższa wygrywa):

1. **Zmienne środowiskowe** — `FORGEJO_URL`, `FORGEJO_TOKEN`,
   `FORGEJO_USERNAME`, `FORGEJO_PASSWORD`
2. `appsettings.{ASPNETCORE_ENVIRONMENT}.json` (obok binara)
3. `appsettings.json` (obok binara) — domyślne wartości pakowane z repo

Parametry i ich odpowiedniki w warstwie POCO
(`src/ForgejoMcp/ForgejoServerOptions.cs`):

| Zmienna ENV                         | Pole w konfiguracji | Wymagana? | Opis                                                        |
| ----------------------------------- | ------------------- | --------- | ----------------------------------------------------------- |
| `FORGEJO_URL`                       | `Forgejo:Url`       | **TAK**   | Korzeń instancji, np. `https://git.example.com`. Klient sam dokleja `/api/v1` (idempotentnie, jeśli już jest). |
| `FORGEJO_TOKEN`                     | `Forgejo:Token`     | jedno z dwóch | Personal Access Token → nagłówek `Authorization: token *** |
| `FORGEJO_USERNAME` + `FORGEJO_PASSWORD` | `Forgejo:Username` + `Forgejo:Password` | jedno z dwóch | Basic auth. Obie muszą być ustawione razem. |
| `FORGEJO_MAX_RETRIES`               | `Forgejo:MaxRetries`| NIE       | Liczba ponowień po pierwszej próbie (domyślnie `3` = łącznie 4 próby). |
| `FORGEJO_RETRY_BASE_DELAY_SECONDS`  | `Forgejo:RetryBaseDelaySeconds` | NIE  | Bazowe opóźnienie startowe backoffu (w sekundach, wykładnicze; domyślnie `1.0`). |

Tryb uwierzytelnienia jest **XOR** (token OR basic) — ustawienie obu zwraca
wyjątek `ArgumentException` w `BuildClient()`, tak więc błędna konfiguracja
jest wykrywana już przy starcie, a nie przy pierwszym wywołaniu narzędzia.
Brak któregokolwiek trybu ⇒ tryb anonimowy (dostęp tylko do publicznych
repozytoriów).

### Przykład `appsettings.json`

```json
{
  "Forgejo": {
    "Url": "https://git.example.com",
    "Token": null,
    "Username": null,
    "Password": null,
    "MaxRetries": 3,
    "RetryBaseDelaySeconds": 1.0
  }
}
```

## Narzędzia (tools)

Pełna lista — parametry opisują dokładnie interfejs w
`src/ForgejoMcp/ForgejoMcpToolSurface.cs`.

| Narzędzie               | Endpoint Forgejo                              | Parametry (krótko)                                                              |
| ----------------------- | --------------------------------------------- | ------------------------------------------------------------------------------- |
| `list_repos`            | `GET /user/repos`                              | `page`, `limit`                                                                  |
| `get_repo`              | `GET /repos/{owner}/{name}`                    | `owner`, `name`                                                                  |
| `create_issue`          | `POST /repos/{owner}/{name}/issues`            | `owner`, `name`, `title`, `body`, `labels[]`, `assignees[]`, `milestone`         |
| `list_issues`           | `GET /repos/{owner}/{name}/issues`             | `owner`, `name`, `state=open\|closed\|all`, `assigned_by[]`, `label[]`, `page`, `limit` |
| `get_issue`             | `GET /repos/{owner}/{name}/issues/{index}`     | `owner`, `name`, `index`                                                         |
| `list_pull_requests`    | `GET /repos/{owner}/{name}/pulls`              | `owner`, `name`, `state`, `assigned_by[]`, `label[]`, `page`, `limit`             |
| `get_pull_request`      | `GET /repos/{owner}/{name}/pulls/{index}`      | `owner`, `name`, `index`                                                         |
| `get_pull_request_files`| `GET /repos/{owner}/{name}/pulls/{index}/files` (+ `.../pulls/{index}.diff`, gdy `show_diff=true`) | `owner`, `name`, `index`, `show_diff` |
| `list_commits`          | `GET /repos/{owner}/{name}/commits`            | `owner`, `name`, `branch`, `page`, `limit`                                        |
| `list_releases`         | `GET /repos/{owner}/{name}/releases`           | `owner`, `name`, `page`, `limit` (meta-dane assetów: nazwa/URL/rozmiar — nigdy treść) |
| `get_file`              | `GET /repos/{owner}/{name}/raw/{path}`         | `owner`, `name`, `path`, `branch`                                                 |
| `list_file_tree`        | `GET /repos/{owner}/{name}/contents/{path}`    | `owner`, `name`, `path` (pusta = katalog główny), `branch`                       |
| `list_branches`         | `GET /repos/{owner}/{name}/branches`           | `owner`, `name` — zwraca `name`, `commit_id`, `commit_message` (1. linia), `commit_url` |
| `get_branch`            | `GET /repos/{owner}/{name}/branches/{name}`    | `owner`, `name`, `branch` (nazwy z `/` też działają)                             |
| `list_labels`           | `GET /repos/{owner}/{name}/labels`             | `owner`, `name` — zwraca `id`, `name`, `color`, `description`                    |
| `create_label`          | `POST /repos/{owner}/{name}/labels`            | `owner`, `name`, `label_name`, `color` (#rrggbb), `description` (opcjonalna) — idempotentne po nazwie, patrz niżej |
| `update_issue`          | `PATCH /repos/{owner}/{name}/issues/{index}`   | `owner`, `name`, `index` + rzadkie (sparse) `title`, `body`, `state`, `assignees`, `clear_assignees`, `milestone_id`, `labels[]` (ids), `add_label_ids[]`, `remove_label_ids[]` |
| `add_issue_comment`     | `POST /repos/{owner}/{name}/issues/{index}/comments` | `owner`, `name`, `index`, `content` (Markdown) — pole na szynie to `body`   |
| `create_pull_request`   | `POST /repos/{owner}/{name}/pulls`             | `owner`, `name`, `title`, `base`, `head` (`owner:branch` = cross-repo), `body`, `draft` — świadomie bez labeli/assignees |

### Paginacja (kontrakt `page` / `next_page`)

Wszystkie narzędzia listujące akceptują `page` (numer strony, od 1,
domyślnie `1`) i `limit` (rozmiar strony, domyślnie 30; góra strony
wystawianej przez instancję: 50). Odpowiedź to koperta:

```jsonc
{ "items": [ ... ], "count": 2, "next_page": null, "total": 2 /* tylko gdy instancja raportuje */ }
```

- `items` / `count` — zawsze obecne.
- `total` — obecna tylko wtedy, gdy instancja wyśle nagłówek
  `x-total-count` (lub pole `total` w ciele). Instancje, które tego nie
  wysyłają, po prostu go pomijają.
- `next_page` — **zawsze obecna**, wartość `int` lub `null`:
  - `null` ⇒ widziano już wszystkie strony (przerywaj iterację),
  - numer ⇒ przekaż go jako `page` kolejnego wywołania tego samego narzędzia.

Wyliczanie `next_page`: (1) jeśli instancja wyśle nagłówek
`Link: <…?page=N>; rel="next"`, bierze się `N`; (2) w przeciwnym razie
strona krótsza niż `limit` ⇒ `null`, a strona pełna (`count == limit`) ⇒
`page + 1`. Dzięki temu listy są wiarygodne też na instancjach, które nie
zwracają `total` (np. ta produkcyjna).

`list_file_tree` nie jest stronowany — katalogi Forgejo/Gitea zwracane są
jako jedna, płaska lista (`{path, entries}`), więc nie ma `page`
/`next_page`. Analogicznie: `list_branches` i `list_labels` zwracają
prostą listę (bez koperty stronowania) — to zestawienia naturalnie
niewielkie, więc nie warto je stronyować.

### Mutacje i idempotentność (Tier 2)

Tier 2 przynosi trzy modyfikujące narzędzia (`create_label`,
`update_issue`, `create_pull_request`) oraz dodające komentarz
`add_issue_comment` — każde ma własną umowę:

**`create_label` — idempotentne po nazwie.** Serwer na naszej instancji
**nie egzekwuje unikalności nazw etykiet** — powtórne `POST /labels` z tą
samą nazwą zwraca 201 i tworzy **dubel** (inne buildy odpowiedzą `409
Conflict`). Dlatego warstwa MCP **pierw listuje etykiety** (`GET
/labels`) i jeśli znajdzie taką samą nazwę, zwraca istniejącą etykietę
bez żadnego `POST`. Obie ścieżki mają identyczną kopertę:

```jsonc
// nowa etykieta (tworzona)
{ "created": true,  "label": { "id": 42, "name": "nowa", "color": "336699", "description": "…" }, "existing": null }
// istniejąca (POST nie wysłany)
{ "created": false, "existing": { "id": 98, "name": "nowa", "color": "336699", "description": "…" }, "label": null }
```

Klient może zawsze rozróżniać `created` bez oglądania statusu HTTP.
Ścieżkę idempotency pokrywają testy
(`CreateLabel_existing_name_returns_created_false_not_an_error`).

**`update_issue` — `PATCH`, nie `POST`/`PUT`.** Test live na
`https://git.home.internal` (2026-08-26):

```
POST   /api/v1/repos/o/n/issues/9  → 405  [Allow: GET, PATCH, DELETE]
PUT    /api/v1/repos/o/n/issues/9  → 405  [Allow: GET, PATCH, DELETE]
PATCH  /api/v1/repos/o/n/issues/9  → 201  {…updated issue…}
```

(Kluczowy wniosek: instancja akceptuje **PATCH** — nie `POST`, nie `PUT`.)
To samo reguły dla PR: `PATCH /pulls/{n} state=closed → 201,
PUT/POST → 405`.

Klient wysyła **rzadkie ciało** (sparse payload) — tylko pola, które
faktycznie mamy zmienić:

- `title` / `body` / `state` (`open`|`closed`) — proste wartości;
- `assignees` — lista logins (semantyka „zamień zestaw”);
  `assignees=[]` jest **przekazywane** tylko gdy
  `clear_assignees=true` — inaczej pusta lista jest odrzucana i nie
  trafia na szynę (serwer zostawia przypisane konta; zapobiega
  przypadkowemu odłączeniu assignee);
- `milestone_id` — id kamienia milowego;
- `labels` — lista **id** etykiet (semantyka „zamień zestaw”, użyj
  `list_labels` najpierw); `add_label_ids` / `remove_label_ids` —
  przyrostowe.

Gdy żaden field nie jest ustawiony klient rzuca `ArgumentException`
**przed** HTTP — narzędzie zwraca
`{"error":{"code":"invalid_request",…}}`, bez uderzenia w serwer.

**`create_pull_request` — błędy 404 / 422.** Gdy `head` (albo `base`)
nie istnieje, instancja odpowiada **404** z ciałem:

```json
{"message":"The target couldn't be found.",
 "errors":["could not find 'ghost-…' to be a commit, branch or tag in the head repository dark-eternity/mcp-forgejo-server"]}
```

Inne buildy Forgejo/Gitea mogą w tych przypadkach zwracać `422`. Oba
kody przechodzą przez `ForgejoException` i są mapowane na stabilny kod
w kopercie błędu:

```jsonc
// 404 →  { "error": { "code": "not_found",
//             "message": "… The `head` or `base` branch does not exist …
//             verify with list_branches before retrying …" } }
// 422 →  { "error": { "code": "http_422",
//             "message": "… server response below …" } }
```

Agent użytkownika może więc deterministycznie rozróżniać
`not_found` (praca z gałęzią) od `http_422` (np. już istniejący PR
z tego samego head) i podpowiedzieć użytkownikowi co naprawić.

**`add_issue_comment` — pole na szynie to `body`, nie `content`.**
Warstwa MCP przyjmuje argument `content` (Markdown) dla czytelności dla
agenta, ale klient Forgejo **wymaga pola `body`** — instancja odrzuca
`{"content": "…"}` z `422 [Body]: Required`. Klient mapuje automatycznie;
testy potwierdzają, że na szynę idzie `body` a **nie** `content`.

### Przykłady wywołań (jsonrpc 2.0 / MCP `tools/call`)

```jsonc
// list_repos — pierwsze 30 repozytoriów
{ "jsonrpc":"2.0","id":2,"method":"tools/call","params":{
    "name":"list_repos","arguments":{"page":1,"limit":30}}}

// get_repo
{ "jsonrpc":"2.0","id":3,"method":"tools/call","params":{
    "name":"get_repo","arguments":{"owner":"dark-eternity","name":"emberfall"}}}

// create_issue — jedyna modyfikująca operacja
{ "jsonrpc":"2.0","id":4,"method":"tools/call","params":{
    "name":"create_issue","arguments":{
        "owner":"dark-eternity","name":"emberfall",
        "title":"Błąd przy logowaniu","body":"Szczegóły…",
        "labels":["bug"],"assignees":["alice"]}}}

// list_issues
{ "jsonrpc":"2.0","id":5,"method":"tools/call","params":{
    "name":"list_issues","arguments":{"owner":"o","name":"n",
        "state":"open","label":["bug"],"page":1}}}

// list_pull_requests
{ "jsonrpc":"2.0","id":6,"method":"tools/call","params":{
    "name":"list_pull_requests","arguments":{"owner":"o","name":"n","state":"all"}}}

// list_commits
{ "jsonrpc":"2.0","id":7,"method":"tools/call","params":{
    "name":"list_commits","arguments":{"owner":"o","name":"n",
        "branch":"main","limit":20}}}

// get_file — treść surowego pliku (UTF-8 ⇒ pole `text`)
{ "jsonrpc":"2.0","id":8,"method":"tools/call","params":{
    "name":"get_file","arguments":{"owner":"o","name":"n","path":"README.md"}}}
```

### Zasoby (resources)

| URI / szablon                      | Opis                                                        |
| ---------------------------------- | ----------------------------------------------------------- |
| `forgejo://instance`               | Tożsamość serwera: instancja URL + tryb uwierzytelnienia.  |
| `forgejo://repo/{owner}/{name}`    | Metadane repo — identyczny dokument co narzędzie `get_repo`.|

`resources/list` raportuje statyczny zasób `instance`; zasób repo jest
eksponowany jako **szablon** (`resources/templates/list`).

## Umowa błędów (error contract)

Narzędzia MCP **nie rzutują wyjątków na poziomie protokołu** — błędy wracają
jako `isError` + ładunek:

```json
{ "error": {
    "code": "not_found|unauthorized|forbidden|rate_limited|transport_error",
    "message": "…"
}}
```

`code` to obniżony kod HTTP Forgejo (np. `not_found` dla 404) albo stabilna
nazwa błędu transportowego — klient może więc rozgałęziać się po tym polu
deterministycznie.

## Inne cechy

- Retry: klient ponawia przejściowe błędy (HTTP 429/5xx, transport) z
  wykładniczym backoff (domyślnie 4 próby); nagłówek `Retry-After`
  jest przestrzegany.
- JSON na szynach jest zawsze `snake_case`, zgodne z API Forgejo.
- Logi tylko na **stderr** — stdout strzeże kanału protokołu MCP.

## Jak użyć jako szablon innych serwerów MCP

Repozytorium jest zaprojektowany, by być **forkiem startowym** dla
dowolnego nowego serwera MCP w .NET 10:

1. Fork repo na Forgejo/Gitea → `Settings` → włącz **Template Repo** (pole
   "Is template") — to pozwala innym użytkownikom tworzyć repo z tego
   szablonu jednym kliknięciem.
2. Nowe repo → zmień nazwę projektu w `Forgejo.Mcp` na `MojaNazwa.Mcp`
   (nazwa pętli w `src/ForgejoMcp/`), w `src/ForgejoMcp/Program.cs`
   zmienij `ServerName`, `ServerInfo.Title` i `ServerInstructions`.
3. Wymień implementacje tools/resource w `MojaNazwaMcpToolSurface` i
   `MojaNazwa/MojaNazwaResources` — struktura (DI + `McpServerTool`
   attribute + zapytania REST) pozostaje identyczna.
4. Konfiguracja: zmień sekcję `"Forgejo"` w `appsettings.json` +
   `ForgejoServerOptions` na własny zestaw zmiennych ENV (np.
   `MY_SERVICE_URL/TOKEN`) — `BuildClient()` to jedyne miejsce walidacji.
5. Testy: `tests/ForgejoMcp.Tests` jest szkieletem — `TestHttpHandler`
   symuluje REST, `ForgejoServerOptionsTests` pokazuje wzorzec testów
   konfiguracji.
6. CI: `.github/workflows/ci.yml` i `.forgejo/workflows/ci.yml` są
   identyczne — usun jeden, jeśli celujesz wyłącznie na GitHuba lub
   Forgejo.

W efekcie masz gotowy, działający, testowany i pokryty CI MCP server w .NET 10
— bez pisania go od zera.

## Licencja

MIT — zobacz [LICENSE](LICENSE).
