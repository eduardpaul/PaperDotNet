// Run inside a disposable checkout of the pinned PaperGo commit (internal package imports).
package main

import (
	"context"
	"encoding/json"
	"flag"
	"fmt"
	"os"
	"papergo/internal/dal/catalog"
	"papergo/internal/dal/database"
	"papergo/internal/dal/surfaces"
)

func main() {
	provider := flag.String("provider", "sqlite", "sqlite or postgresql")
	connection := flag.String("connection", "", "SQLite path or PostgreSQL URL")
	itemID := flag.String("item", "", "item written by Entity Framework")
	flag.Parse()
	if *connection == "" {
		*connection = os.Getenv("PAPERGO_INTEROP_CONNECTION")
	}
	ctx := context.Background()
	var db *database.Database
	var err error
	if *provider == "postgresql" {
		db, err = database.OpenPostgres(ctx, *connection)
	} else {
		db, err = database.Open(ctx, *connection)
	}
	must(err)
	defer db.Close()
	must(db.CheckSchema(ctx))
	item, err := db.DAL.Resources().Resource(ctx, *itemID)
	must(err)
	if item.HeadRevisionID == nil {
		panic("EF item has no head")
	}
	revision, err := db.DAL.Revisions().Revision(ctx, *item.HeadRevisionID)
	must(err)
	schema, err := db.DAL.Catalog().Schema(ctx, revision.SchemaRevisionID)
	must(err)
	projections, err := db.DAL.Surfaces().Surfaces(ctx, surfaces.Filter{ItemID: item.ID})
	must(err)
	if len(projections) == 0 {
		panic("EF item has no projection")
	}
	fields, err := db.DAL.Surfaces().FieldValues(ctx, surfaces.ValueFilter{ItemID: item.ID})
	must(err)
	before := item.UpdatedAt
	changed, err := db.DAL.Resources().SetArchived(ctx, item.ID, item.Version, "papergo-interop", true)
	must(err)
	if !changed {
		panic("expected an active EF item")
	}
	item, err = db.DAL.Resources().Resource(ctx, item.ID)
	must(err)
	if !item.UpdatedAt.Equal(before) {
		panic("Go archive changed content timestamp")
	}
	nextSchema, err := db.DAL.Catalog().RecordSchema(ctx, schema.ContainerID, "papergo-interop", schema.Definition)
	must(err)
	schemas, err := db.DAL.Catalog().Schemas(ctx, catalog.SchemaFilter{ContainerID: schema.ContainerID})
	must(err)
	must(json.NewEncoder(os.Stdout).Encode(map[string]any{
		"schema_compatible": true, "item": item, "revision": revision,
		"surfaces": projections, "indexed_fields": len(fields), "schema_revisions": len(schemas), "new_schema_id": nextSchema.ID,
	}))
}
func must(err error) {
	if err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
}
