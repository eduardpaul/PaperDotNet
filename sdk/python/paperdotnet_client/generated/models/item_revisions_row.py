from __future__ import annotations
import datetime
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union

@dataclass
class ItemRevisionsRow(AdditionalDataHolder, Parsable):
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # The blobId property
    blob_id: Optional[str] = None
    # The containerId property
    container_id: Optional[str] = None
    # The contentTypeId property
    content_type_id: Optional[str] = None
    # The createdAt property
    created_at: Optional[datetime.datetime] = None
    # The createdBy property
    created_by: Optional[str] = None
    # The filename property
    filename: Optional[str] = None
    # The id property
    id: Optional[str] = None
    # The itemId property
    item_id: Optional[str] = None
    # The keywords property
    keywords: Optional[str] = None
    # The name property
    name: Optional[str] = None
    # The payload property
    payload: Optional[str] = None
    # The revisionNumber property
    revision_number: Optional[int] = None
    # The schemaRevisionId property
    schema_revision_id: Optional[str] = None

    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> ItemRevisionsRow:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: ItemRevisionsRow
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return ItemRevisionsRow()

    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        fields: dict[str, Callable[[Any], None]] = {
            "blobId": lambda n : setattr(self, 'blob_id', n.get_str_value()),
            "containerId": lambda n : setattr(self, 'container_id', n.get_str_value()),
            "contentTypeId": lambda n : setattr(self, 'content_type_id', n.get_str_value()),
            "createdAt": lambda n : setattr(self, 'created_at', n.get_datetime_value()),
            "createdBy": lambda n : setattr(self, 'created_by', n.get_str_value()),
            "filename": lambda n : setattr(self, 'filename', n.get_str_value()),
            "id": lambda n : setattr(self, 'id', n.get_str_value()),
            "itemId": lambda n : setattr(self, 'item_id', n.get_str_value()),
            "keywords": lambda n : setattr(self, 'keywords', n.get_str_value()),
            "name": lambda n : setattr(self, 'name', n.get_str_value()),
            "payload": lambda n : setattr(self, 'payload', n.get_str_value()),
            "revisionNumber": lambda n : setattr(self, 'revision_number', n.get_int_value()),
            "schemaRevisionId": lambda n : setattr(self, 'schema_revision_id', n.get_str_value()),
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
        writer.write_str_value("blobId", self.blob_id)
        writer.write_str_value("containerId", self.container_id)
        writer.write_str_value("contentTypeId", self.content_type_id)
        writer.write_datetime_value("createdAt", self.created_at)
        writer.write_str_value("createdBy", self.created_by)
        writer.write_str_value("filename", self.filename)
        writer.write_str_value("id", self.id)
        writer.write_str_value("itemId", self.item_id)
        writer.write_str_value("keywords", self.keywords)
        writer.write_str_value("name", self.name)
        writer.write_str_value("payload", self.payload)
        writer.write_int_value("revisionNumber", self.revision_number)
        writer.write_str_value("schemaRevisionId", self.schema_revision_id)
        writer.write_additional_data_value(self.additional_data)


