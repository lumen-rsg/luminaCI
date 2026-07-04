-- Lumina CI Database Initialization
--
-- Creates schemas + grants permissions ONLY. Tables are created by each service
-- at startup via EF Core migrations (DatabaseInitializer in Lumina.Web.Shared),
-- so this script no longer touches any table or column — it owns nothing that a
-- service migration owns, removing the old ordering dependency between this
-- script and the services' first boot.

-- Enable required PostgreSQL extensions (hstore is also re-asserted idempotently
-- by the BuildService migration, but it must exist before any service connects
-- because hstore columns are part of the build schema).
CREATE EXTENSION IF NOT EXISTS hstore;

CREATE SCHEMA IF NOT EXISTS build;
CREATE SCHEMA IF NOT EXISTS security;
CREATE SCHEMA IF NOT EXISTS scanner;
CREATE SCHEMA IF NOT EXISTS repository;
CREATE SCHEMA IF NOT EXISTS audit;
CREATE SCHEMA IF NOT EXISTS auth;
CREATE SCHEMA IF NOT EXISTS source;

GRANT ALL ON SCHEMA build TO lumina;
GRANT ALL ON SCHEMA security TO lumina;
GRANT ALL ON SCHEMA scanner TO lumina;
GRANT ALL ON SCHEMA repository TO lumina;
GRANT ALL ON SCHEMA audit TO lumina;
GRANT ALL ON SCHEMA auth TO lumina;
GRANT ALL ON SCHEMA source TO lumina;

-- Grant permissions for tables and sequences in each schema
ALTER DEFAULT PRIVILEGES IN SCHEMA build GRANT ALL ON TABLES TO lumina;
ALTER DEFAULT PRIVILEGES IN SCHEMA build GRANT ALL ON SEQUENCES TO lumina;
ALTER DEFAULT PRIVILEGES IN SCHEMA security GRANT ALL ON TABLES TO lumina;
ALTER DEFAULT PRIVILEGES IN SCHEMA security GRANT ALL ON SEQUENCES TO lumina;
ALTER DEFAULT PRIVILEGES IN SCHEMA scanner GRANT ALL ON TABLES TO lumina;
ALTER DEFAULT PRIVILEGES IN SCHEMA scanner GRANT ALL ON SEQUENCES TO lumina;
ALTER DEFAULT PRIVILEGES IN SCHEMA repository GRANT ALL ON TABLES TO lumina;
ALTER DEFAULT PRIVILEGES IN SCHEMA repository GRANT ALL ON SEQUENCES TO lumina;
ALTER DEFAULT PRIVILEGES IN SCHEMA audit GRANT ALL ON TABLES TO lumina;
ALTER DEFAULT PRIVILEGES IN SCHEMA audit GRANT ALL ON SEQUENCES TO lumina;
ALTER DEFAULT PRIVILEGES IN SCHEMA auth GRANT ALL ON TABLES TO lumina;
ALTER DEFAULT PRIVILEGES IN SCHEMA auth GRANT ALL ON SEQUENCES TO lumina;
ALTER DEFAULT PRIVILEGES IN SCHEMA source GRANT ALL ON TABLES TO lumina;
ALTER DEFAULT PRIVILEGES IN SCHEMA source GRANT ALL ON SEQUENCES TO lumina;

-- EF Core records applied migrations in a shared __EFMigrationsHistory table
-- (all six contexts share the single lumina_ci database, keyed by unique
-- migration id). Pre-create it and grant access so the app role can populate it.
CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
    "MigrationId" character varying(150) NOT NULL,
    "ProductVersion" character varying(32) NOT NULL,
    CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
);
GRANT ALL ON TABLE "__EFMigrationsHistory" TO lumina;
