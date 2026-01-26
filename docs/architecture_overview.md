## Architecture & stack

### Backend

* **.NET 10 / ASP.NET Core**
* Aspire for local development
* Custom control-plane APIs (not kubectl wrappers)
* Kubernetes official **C# client**
* **MongoDB**
* **RabbitMQ** (events, outbox patterns)
* **Hangfire + background hosted services**
* Strong focus on:

    * Transactions
    * Event reliability
    * Idempotency
    * Resource-oriented APIs

### Auth

* **Keycloak**
* OIDC / JWT
* Invite-only user model

### Frontend

* Vite-based SPA
* Talks only to Pole API
* Clean separation from auth provider
