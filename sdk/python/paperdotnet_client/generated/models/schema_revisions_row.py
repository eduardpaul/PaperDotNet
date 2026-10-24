from __future__ import annotations
import datetime
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union

@dataclass
class SchemaRevisionsRow(AdditionalDataHolder, Parsable):
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # The containerId property
    container_id: Optional[str] = None
    # The createdAt property
    created_at: Optional[datetime.datetime] = None
    # The createdBy property
    created_by: Optional[str] = None
    # The definition property
    definition: Optional[str] = None
    # The id property
    id: Optional[str] = None
    # The revisionNumber property
    revision_number: Optional[int] = None

    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> SchemaRevisionsRow:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: SchemaRevisionsRow
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return SchemaRevisionsRow()

    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        fields: dict[str, Callable[[Any], None]] = {
            "containerId": lambda n : setattr(self, 'container_id', n.get_str_value()),
            "createdAt": lambda n : setattr(self, 'created_at', n.get_datetime_value()),
            "createdBy": lambda n : setattr(self, 'created_by', n.get_str_value()),
            "definition": lambda n : setattr(self, 'definition', n.get_str_value()),
            "id": lambda n : setattr(self, 'id', n.get_str_value()),
            "revisionNumber": lambda n : setattr(self, 'revision_number', n.get_int_value()),
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
        writer.write_str_value("containerId", self.container_id)
        writer.write_datetime_value("createdAt", self.created_at)
        writer.write_str_value("createdBy", self.created_by)
        writer.write_str_value("definition", self.definition)
        writer.write_str_value("id", self.id)
        writer.write_int_value("revisionNumber", self.revision_number)
        writer.write_additional_data_value(self.additional_data)


