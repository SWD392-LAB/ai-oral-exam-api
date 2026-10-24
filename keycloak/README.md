# AIVES Keycloak server (local Docker)

This setup runs the Keycloak server that signs users in for AIVES. The API stores
no passwords: it validates the access tokens issued by this realm (`Keycloak`
section in `src/AiOralExam.Api/appsettings.json`).

## Start and stop

Run from `ai-oral-exam-api`:

```sh
docker compose up -d keycloak
docker compose ps
docker compose logs --tail=100 keycloak
```

The first command starts Keycloak 26.8.0, a separate PostgreSQL database for
Keycloak, and Mailpit for local password-reset email. It does not start the
application PostgreSQL or pgAdmin services.

Stop these services while preserving accounts and configuration:

```sh
docker compose stop keycloak keycloak-db mailpit
```

## Access

| Service | URL | Local credentials |
| --- | --- | --- |
| Admin Console | http://localhost:8080/admin/ | `admin` / `admin-dev-only` |
| AIVES Account Console | http://localhost:8080/realms/aives/account/ | Demo username or email / `Password@123` |
| Development email inbox | http://localhost:8025 | No login |
| OIDC discovery | http://localhost:8080/realms/aives/.well-known/openid-configuration | Public |

The bootstrap administrator belongs to the `master` realm. In the Admin Console,
use the realm selector to switch to **aives** to see the application users,
clients, and roles. An AIVES `Administrator` role is separate from server
administration rights.

## Imported configuration

- Realm: `aives`.
- Realm roles: `Student`, `Lecturer`, `Administrator`.
- `aives-web`: public OIDC client with Authorization Code and PKCE S256;
  redirect URLs on `http://localhost:5173/*`.
- `aives-swagger`: public OIDC client with PKCE; redirect URLs
  `http://localhost:5080/swagger/oauth2-redirect.html` and
  `https://localhost:7080/swagger/oauth2-redirect.html`.
- Realm roles are emitted as a flat `role` claim in access tokens, matching the
  API's `RoleClaimType`.
- `aives-api`: bearer-only client, included as the access-token audience.
- `aives-admin-api`: confidential client-credentials client used by the API to
  administer users; its service account has only the required `realm-management`
  roles (`manage-users`, `view-users`, `query-users`, `view-realm`).
- Password grants and implicit flow are disabled for the public clients.
- Access tokens expire after five minutes; brute-force protection is enabled.
- Self-registration is disabled; password reset is enabled through Mailpit.
- 48 demo accounts are provisioned from the project's seed profiles. Each Keycloak
  username equals `users.username`; users can sign in with the username or the email.

Useful demo accounts, all using `Password@123`:

| Username | Email | Realm role |
| --- | --- | --- |
| han.hg | han.hg@aives.edu.vn | Student |
| an.nv | an.nv@aives.edu.vn | Lecturer |
| admin | admin@aives.edu.vn | Administrator |

Khoa's temporary password requires a change at first sign-in. The locked student
account is disabled. Google sign-in is not configured; it requires your own
Google OAuth credentials. Demo account IDs equal the `users.id` values in
`database/02_seed.sql`; the API finds the user from the token's `sub` claim.

Test sign-in directly in the AIVES Account Console. Password-reset messages appear
in the local Mailpit inbox instead of being delivered to real email addresses.

## Persistence and changes

Keycloak stores its data in the separate `keycloak-data` Docker volume. The
application database uses its existing `pgdata` volume.

`aives-realm.json` is imported on startup only when the realm does not already
exist. Editing this file does not update a running or existing realm. Apply
changes to the existing realm in the Admin Console. In particular, create the
admin client manually for an existing realm:

1. In the **aives** realm, open **Clients** and create an OpenID Connect client
   with ID `aives-admin-api`.
2. Enable **Client authentication** and **Service accounts roles**. Disable
   **Standard flow**, **Direct access grants**, and **Implicit flow**, then save.
3. Set the API's local development secret with:
   `dotnet user-secrets set "KeycloakAdmin:ClientSecret" "dev-aives-admin-api-secret" --project src/AiOralExam.Api`.
   This value matches `aives-realm.json` and is for development only; do not
   store it in any `appsettings*.json` file.
4. On **Service account roles**, select the `realm-management` client and assign
   `manage-users`, `view-users`, `query-users`, and `view-realm`.

The client-credentials secret is for local development only; never log it or
reuse it in a deployed environment. Avoid `docker compose down -v` when you want
to preserve data: it deletes the Compose project's volumes.

Optional Compose environment variables:

- `KEYCLOAK_ADMIN_USERNAME` and `KEYCLOAK_ADMIN_PASSWORD`: bootstrap administrator.
- `KEYCLOAK_DB_PASSWORD`: password used to initialize the separate database.

These variables initialize credentials; changing them later does not automatically
rotate credentials in an existing database or administrator account.

This configuration uses `start-dev` and demo credentials for local development.
For deployment, configure HTTPS, a real hostname, database credentials, and SMTP,
and run Keycloak in production mode.

Reference: [Keycloak containers](https://www.keycloak.org/server/containers).
