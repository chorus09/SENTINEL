CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
    "MigrationId" character varying(150) NOT NULL,
    "ProductVersion" character varying(32) NOT NULL,
    CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
);

START TRANSACTION;
CREATE TABLE monitors (
    "Id" uuid NOT NULL,
    "Name" character varying(100) NOT NULL,
    "Slug" character varying(100) NOT NULL,
    "Url" character varying(2048) NOT NULL,
    "IntervalSeconds" integer NOT NULL,
    "TimeoutSeconds" integer NOT NULL,
    "IsEnabled" boolean NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    "NextCheckAt" timestamp with time zone,
    CONSTRAINT "PK_monitors" PRIMARY KEY ("Id")
);

CREATE UNIQUE INDEX "IX_monitors_Slug" ON monitors ("Slug");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20261026161523_InitialMonitorSchema', '10.0.12');

COMMIT;
