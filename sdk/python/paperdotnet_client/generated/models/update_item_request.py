from __future__ import annotations
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union
from uuid import UUID

if TYPE_CHECKING:
    from .update_item_request_fields import UpdateItemRequest_fields

@dataclass
class UpdateItemRequest(AdditionalDataHolder, Parsable):
    """
    PATCH body (documented shape; the handler reads raw JSON to tell a missing `parentId` from null): `fields`are merged (null removes a value), `parentId` moves the item (null: the list root).
    """
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # The contentTypeId property
    content_type_id: Optional[UUID] = None
    # The fields property
    fields: Optional[UpdateItemRequest_fields] = None
    # The parentId property
    parent_id: Optional[UUID] = None
    
    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> UpdateItemRequest:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: UpdateItemRequest
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return UpdateItemRequest()
    
    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        from .update_item_request_fields import UpdateItemRequest_fields

        from .update_item_request_fields import UpdateItemRequest_fields

        fields: dict[str, Callable[[Any], None]] = {
            "contentTypeId": lambda n : setattr(self, 'content_type_id', n.get_uuid_value()),
            "fields": lambda n : setattr(self, 'fields', n.get_object_value(UpdateItemRequest_fields)),
            "parentId": lambda n : setattr(self, 'parent_id', n.get_uuid_value()),
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
        writer.write_uuid_value("contentTypeId", self.content_type_id)
        writer.write_object_value("fields", self.fields)
        writer.write_uuid_value("parentId", self.parent_id)
        writer.write_additional_data_value(self.additional_data)
    

