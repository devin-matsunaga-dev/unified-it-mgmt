# Demo data

The `Seeder` console project runs automatically under Aspire after `web-host` is healthy. It applies
pending Platform migrations and idempotently creates three sites, four departments, and 20 user
profiles. A later run reports zero additions and does not duplicate or overwrite existing records.

The matching development-only identities are declared in
`src/AppHost/Keycloak/it-platform-realm.json`: two Admins, four Technicians, four Managers, and ten
EndUsers. Usernames are `admin1`–`admin2`, `technician1`–`technician4`, `manager1`–`manager4`, and
`enduser1`–`enduser10`. Their initial passwords follow the existing realm-import pattern, for example
`admin1` uses `ChangeMe-Admin1!` and `enduser10` uses `ChangeMe-EndUser10!`.

Keycloak only imports a realm when it does not already exist. If the development volume predates
WP-0.8, remove the `it-platform-keycloak-data` Docker volume before starting Aspire to recreate the
development realm with all 20 identities. This deletes only the persisted local development realm.

To run the database seeder directly:

```bash
ConnectionStrings__database='<postgres connection string>' dotnet run --project src/Seeder/Seeder.csproj
```
