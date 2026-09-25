from __future__ import annotations
import datetime
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union
from uuid import UUID

if TYPE_CHECKING:
    from .json_object import JsonObject

@dataclass
class ItemVersionResponse(AdditionalDataHolder, Parsable):
    """
    A version of an item; `fields` holds the values after that change (including `title`).
    """
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # The changedFields property
    changed_fields: Optional[list[str]] = None
    # The contentTypeId property
    content_type_id: Optional[UUID] = None
    # The createdAt property
    created_at: Optional[datetime.datetime] = None
    # The createdBy property
    created_by: Optional[UUID] = None
    # The fields property
    fields: Optional[JsonObject] = None
    # The id property
    id: Optional[UUID] = None
    # The isCurrent property
    is_current: Optional[bool] = None
    # The number property
    number: Optional[int] = None
    
    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> ItemVersionResponse:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: ItemVersionResponse
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return ItemVersionResponse()
    
    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        from .json_object import JsonObject

        from .json_object import JsonObject

        fields: dict[str, Callable[[Any], None]] = {
            "changedFields": lambda n : setattr(self, 'changed_fields', n.get_collection_of_primitive_values(str)),
            "contentTypeId": lambda n : setattr(self, 'content_type_id', n.get_uuid_value()),
            "createdAt": lambda n : setattr(self, 'created_at', n.get_datetime_value()),
            "createdBy": lambda n : setattr(self, 'created_by', n.get_uuid_value()),
            "fields": lambda n : setattr(self, 'fields', n.get_object_value(JsonObject)),
            "id": lambda n : setattr(self, 'id', n.get_uuid_value()),
            "isCurrent": lambda n : setattr(self, 'is_current', n.get_bool_value()),
            "number": lambda n : setattr(self, 'number', n.get_int_value()),
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
        writer.write_uuid_value("contentTypeId", self.content_type_id)
        writer.write_datetime_value("createdAt", self.created_at)
        writer.write_uuid_value("createdBy", self.created_by)
        writer.write_object_value("fields", self.fields)
        writer.write_uuid_value("id", self.id)
        writer.write_bool_value("isCurrent", self.is_current)
        writer.write_int_value("number", self.number)
        writer.write_additional_data_value(self.additional_data)
    

