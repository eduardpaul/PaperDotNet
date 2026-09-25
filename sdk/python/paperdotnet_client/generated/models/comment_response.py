from __future__ import annotations
import datetime
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union
from uuid import UUID

@dataclass
class CommentResponse(AdditionalDataHolder, Parsable):
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # The createdAt property
    created_at: Optional[datetime.datetime] = None
    # The createdBy property
    created_by: Optional[UUID] = None
    # The id property
    id: Optional[UUID] = None
    # The itemId property
    item_id: Optional[UUID] = None
    # The mentions property
    mentions: Optional[list[UUID]] = None
    # The parentId property
    parent_id: Optional[UUID] = None
    # The text property
    text: Optional[str] = None
    # The updatedAt property
    updated_at: Optional[datetime.datetime] = None
    # The updatedBy property
    updated_by: Optional[UUID] = None
    
    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> CommentResponse:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: CommentResponse
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return CommentResponse()
    
    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        fields: dict[str, Callable[[Any], None]] = {
            "createdAt": lambda n : setattr(self, 'created_at', n.get_datetime_value()),
            "createdBy": lambda n : setattr(self, 'created_by', n.get_uuid_value()),
            "id": lambda n : setattr(self, 'id', n.get_uuid_value()),
            "itemId": lambda n : setattr(self, 'item_id', n.get_uuid_value()),
            "mentions": lambda n : setattr(self, 'mentions', n.get_collection_of_primitive_values(UUID)),
            "parentId": lambda n : setattr(self, 'parent_id', n.get_uuid_value()),
            "text": lambda n : setattr(self, 'text', n.get_str_value()),
            "updatedAt": lambda n : setattr(self, 'updated_at', n.get_datetime_value()),
            "updatedBy": lambda n : setattr(self, 'updated_by', n.get_uuid_value()),
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
        writer.write_datetime_value("createdAt", self.created_at)
        writer.write_uuid_value("createdBy", self.created_by)
        writer.write_uuid_value("id", self.id)
        writer.write_uuid_value("itemId", self.item_id)
        writer.write_collection_of_primitive_values("mentions", self.mentions)
        writer.write_uuid_value("parentId", self.parent_id)
        writer.write_str_value("text", self.text)
        writer.write_datetime_value("updatedAt", self.updated_at)
        writer.write_uuid_value("updatedBy", self.updated_by)
        writer.write_additional_data_value(self.additional_data)
    

