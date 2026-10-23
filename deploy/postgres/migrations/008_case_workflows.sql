ALTER TABLE cx_team ADD COLUMN description text NOT NULL DEFAULT '';

CREATE TABLE cx_workflow (
 id uuid PRIMARY KEY, tenant_id varchar(64) NOT NULL, name varchar(128) NOT NULL,
 description text NOT NULL DEFAULT '', topic varchar(128), stages jsonb NOT NULL,
 enabled boolean NOT NULL DEFAULT true, version integer NOT NULL DEFAULT 1,
 created_at timestamptz NOT NULL DEFAULT now(), updated_at timestamptz NOT NULL DEFAULT now(),
 UNIQUE(tenant_id,id), CHECK(jsonb_typeof(stages)='array' AND jsonb_array_length(stages) BETWEEN 1 AND 20)
);
CREATE INDEX cx_workflow_tenant ON cx_workflow(tenant_id,enabled);

ALTER TABLE cx_case ADD COLUMN workflow_id uuid;
ALTER TABLE cx_case ADD COLUMN workflow_snapshot jsonb NOT NULL DEFAULT '{}';
ALTER TABLE cx_case ADD COLUMN stage_index integer NOT NULL DEFAULT -1 CHECK(stage_index>=-1);
ALTER TABLE cx_case ADD COLUMN routing_reason text NOT NULL DEFAULT '';
ALTER TABLE cx_case ADD COLUMN routing_model varchar(256);
ALTER TABLE cx_case ADD CONSTRAINT cx_case_workflow FOREIGN KEY(tenant_id,workflow_id) REFERENCES cx_workflow(tenant_id,id);

ALTER TABLE cx_case_activity ADD COLUMN team_id uuid;
ALTER TABLE cx_case_activity ADD COLUMN stage_title varchar(128);
