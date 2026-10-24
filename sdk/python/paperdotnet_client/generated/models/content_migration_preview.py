from __future__ import annotations
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union

@dataclass
class ContentMigrationPreview(AdditionalDataHolder, Parsable):
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # The alreadyImported property
    already_imported: Optional[bool] = None
    # The blockers property
    blockers: Optional[list[str]] = None
    # The collections property
    collections: Optional[int] = None
    # The files property
    files: Optional[int] = None
    # The historyEntries property
    history_entries: Optional[int] = None
    # The items property
    items: Optional[int] = None
    # The name property
    name: Optional[str] = None
    # The sourceHash property
    source_hash: Optional[str] = None
    # The workspaceId property
    workspace_id: Optional[str] = None

    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> ContentMigrationPreview:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: ContentMigrationPreview
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return ContentMigrationPreview()

    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        fields: dict[str, Callable[[Any], None]] = {
            "alreadyImported": lambda n : setattr(self, 'already_imported', n.get_bool_value()),
            "blockers": lambda n : setattr(self, 'blockers', n.get_collection_of_primitive_values(str)),
            "collections": lambda n : setattr(self, 'collections', n.get_int_value()),
            "files": lambda n : setattr(self, 'files', n.get_int_value()),
            "historyEntries": lambda n : setattr(self, 'history_entries', n.get_int_value()),
            "items": lambda n : setattr(self, 'items', n.get_int_value()),
            "name": lambda n : setattr(self, 'name', n.get_str_value()),
            "sourceHash": lambda n : setattr(self, 'source_hash', n.get_str_value()),
            "workspaceId": lambda n : setattr(self, 'workspace_id', n.get_str_value()),
        }
        return fields

    def serialize(self,writer: SerializationWriter) -> None:
        """
        Serializes information the current object
        param writer: Serialization writer to use to serialize this model
        Returns: None
        """
        if writer is None:
            raise TypeError("writer cannot be null.")
        writer.write_bool_value("alreadyImported", self.already_imported)
        writer.write_collection_of_primitive_values("blockers", self.blockers)
        writer.write_int_value("collections", self.collections)
        writer.write_int_value("files", self.files)
        writer.write_int_value("historyEntries", self.history_entries)
        writer.write_int_value("items", self.items)
        writer.write_str_value("name", self.name)
        writer.write_str_value("sourceHash", self.source_hash)
        writer.write_str_value("workspaceId", self.workspace_id)
        writer.write_additional_data_value(self.additional_data)


