from __future__ import annotations
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union

if TYPE_CHECKING:
    from .item_revisions_row import ItemRevisionsRow
    from .schema_revisions_row import SchemaRevisionsRow

@dataclass
class ContentHistoryView(AdditionalDataHolder, Parsable):
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # The changedFields property
    changed_fields: Optional[list[str]] = None
    # The number property
    number: Optional[int] = None
    # The revision property
    revision: Optional[ItemRevisionsRow] = None
    # The schema property
    schema: Optional[SchemaRevisionsRow] = None

    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> ContentHistoryView:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: ContentHistoryView
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return ContentHistoryView()

    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        from .item_revisions_row import ItemRevisionsRow
        from .schema_revisions_row import SchemaRevisionsRow

        from .item_revisions_row import ItemRevisionsRow
        from .schema_revisions_row import SchemaRevisionsRow

        fields: dict[str, Callable[[Any], None]] = {
            "changedFields": lambda n : setattr(self, 'changed_fields', n.get_collection_of_primitive_values(str)),
            "number": lambda n : setattr(self, 'number', n.get_int_value()),
            "revision": lambda n : setattr(self, 'revision', n.get_object_value(ItemRevisionsRow)),
            "schema": lambda n : setattr(self, 'schema', n.get_object_value(SchemaRevisionsRow)),
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
        writer.write_collection_of_primitive_values("changedFields", self.changed_fields)
        writer.write_int_value("number", self.number)
        writer.write_object_value("revision", self.revision)
        writer.write_object_value("schema", self.schema)
        writer.write_additional_data_value(self.additional_data)


