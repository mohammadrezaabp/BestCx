# PostgreSQL

## Migrations

Scripts `migrations/` They are executed with numerical naming and in order. Program in startup
Put them in one transaction And behind one advisory lock applies, therefore several instance
They do not cause problems at the same time.

- After each script is executed in the table `schema_migration` With checksum is registered.
- **Edit one migration Executed gives an error.** For a change schema, add new file.
- To turn off automatic execution: `CX_Database__RunMigrationsOnStartup=false`.

## Required settings

For Debezium (Optional, if used CDC Instead outbox relay):

```
wal_level = logical
max_replication_slots = 4
max_wal_senders = 4
```

## Maintenance

Table `cx_outbox` With outbox relay It is not erased; Published rows remain as long as possible
Be a review. One job Add a cleanup period:

```sql
DELETE FROM cx_outbox
WHERE published_at IS NOT NULL
  AND published_at < now() - INTERVAL '7 days';
```

Table `cx_event` is the largest table. As the volume grows, partition Based on the moon `occurred_at`
..
