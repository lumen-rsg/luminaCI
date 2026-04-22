-- Lumina CI Database Initialization
-- Only creates schemas — tables are created by each service via EF Core EnsureCreatedAsync()

CREATE SCHEMA IF NOT EXISTS build;
CREATE SCHEMA IF NOT EXISTS security;
CREATE SCHEMA IF NOT EXISTS scanner;
CREATE SCHEMA IF NOT EXISTS repository;
CREATE SCHEMA IF NOT EXISTS audit;

GRANT ALL ON SCHEMA build TO lumina;
GRANT ALL ON SCHEMA security TO lumina;
GRANT ALL ON SCHEMA scanner TO lumina;
GRANT ALL ON SCHEMA repository TO lumina;
GRANT ALL ON SCHEMA audit TO lumina;

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

-- Add Git integration columns to pipelines (idempotent)
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='build' AND table_name='pipelines' AND column_name='GitRepoUrl') THEN
        ALTER TABLE build.pipelines ADD COLUMN "GitRepoUrl" text NULL;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='build' AND table_name='pipelines' AND column_name='GitBranch') THEN
        ALTER TABLE build.pipelines ADD COLUMN "GitBranch" text NULL;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='build' AND table_name='pipelines' AND column_name='SpecPath') THEN
        ALTER TABLE build.pipelines ADD COLUMN "SpecPath" text NULL;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='build' AND table_name='pipelines' AND column_name='WebhookSecret') THEN
        ALTER TABLE build.pipelines ADD COLUMN "WebhookSecret" text NULL;
    END IF;
END$$;
