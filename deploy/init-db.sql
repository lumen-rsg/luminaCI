-- Lumina CI Database Initialization
-- Only creates schemas — tables are created by each service via EF Core EnsureCreatedAsync()

-- Enable required PostgreSQL extensions
CREATE EXTENSION IF NOT EXISTS hstore;

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

-- Add Git integration columns to pipelines (idempotent, safe when table doesn't exist yet)
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema='build' AND table_name='pipelines') THEN
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
        IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='build' AND table_name='pipelines' AND column_name='BuildImage') THEN
            ALTER TABLE build.pipelines ADD COLUMN "BuildImage" text NULL;
        END IF;
        IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='build' AND table_name='pipelines' AND column_name='GitUsername') THEN
            ALTER TABLE build.pipelines ADD COLUMN "GitUsername" text NULL;
        END IF;
        IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='build' AND table_name='pipelines' AND column_name='GitToken') THEN
            ALTER TABLE build.pipelines ADD COLUMN "GitToken" text NULL;
        END IF;
        IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='build' AND table_name='pipelines' AND column_name='Tags') THEN
            ALTER TABLE build.pipelines ADD COLUMN "Tags" text[] DEFAULT '{}';
        END IF;
    END IF;
EXCEPTION WHEN undefined_table THEN
    -- Table doesn't exist yet (services haven't created it) — skip safely
    RAISE NOTICE 'build.pipelines table does not exist yet, skipping column migrations';
END$$;

-- Add Configuration hstore column to pipeline_steps (idempotent, safe when table doesn't exist yet)
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema='build' AND table_name='pipeline_steps') THEN
        IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='build' AND table_name='pipeline_steps' AND column_name='Configuration') THEN
            ALTER TABLE build.pipeline_steps ADD COLUMN "Configuration" hstore DEFAULT '';
        END IF;
    END IF;
EXCEPTION WHEN undefined_table THEN
    RAISE NOTICE 'build.pipeline_steps table does not exist yet, skipping column migrations';
END$$;
