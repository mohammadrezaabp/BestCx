-- Login accounts are separate from customer profiles; credentials never live in profile attributes.
CREATE TABLE app_user (
    id uuid PRIMARY KEY,
    tenant_id varchar(64) NOT NULL,
    username varchar(128) NOT NULL,
    password_hash text NOT NULL,
    role varchar(16) NOT NULL CHECK (role IN ('customer', 'admin')),
    customer_id varchar(128),
    display_name varchar(256),
    enabled boolean NOT NULL DEFAULT true,
    security_stamp uuid NOT NULL,
    failed_attempts integer NOT NULL DEFAULT 0,
    locked_until timestamptz,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    FOREIGN KEY (tenant_id, customer_id) REFERENCES customer_identity (tenant_id, customer_id),
    CHECK ((role = 'customer' AND customer_id IS NOT NULL) OR (role = 'admin' AND customer_id IS NULL)),
    UNIQUE (tenant_id, customer_id)
);
CREATE UNIQUE INDEX app_user_username ON app_user (tenant_id, lower(username));
