from __future__ import annotations
import datetime
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union
from uuid import UUID

@dataclass
class NotificationResponse(AdditionalDataHolder, Parsable):
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # The body property
    body: Optional[str] = None
    # The createdAt property
    created_at: Optional[datetime.datetime] = None
    # The id property
    id: Optional[UUID] = None
    # The itemId property
    item_id: Optional[UUID] = None
    # The listId property
    list_id: Optional[UUID] = None
    # The readAt property
    read_at: Optional[datetime.datetime] = None
    # The title property
    title: Optional[str] = None
    # The type property
    type: Optional[str] = None
    # The workspaceId property
    workspace_id: Optional[UUID] = None
    
    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> NotificationResponse:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: NotificationResponse
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return NotificationResponse()
    
    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        fields: dict[str, Callable[[Any], None]] = {
            "body": lambda n : setattr(self, 'body', n.get_str_value()),
            "createdAt": lambda n : setattr(self, 'created_at', n.get_datetime_value()),
            "id": lambda n : setattr(self, 'id', n.get_uuid_value()),
            "itemId": lambda n : setattr(self, 'item_id', n.get_uuid_value()),
            "listId": lambda n : setattr(self, 'list_id', n.get_uuid_value()),
            "readAt": lambda n : setattr(self, 'read_at', n.get_datetime_value()),
            "title": lambda n : setattr(self, 'title', n.get_str_value()),
            "type": lambda n : setattr(self, 'type', n.get_str_value()),
            "workspaceId": lambda n : setattr(self, 'workspace_id', n.get_uuid_value()),
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
        writer.write_str_value("body", self.body)
        writer.write_datetime_value("createdAt", self.created_at)
        writer.write_uuid_value("id", self.id)
        writer.write_uuid_value("itemId", self.item_id)
        writer.write_uuid_value("listId", self.list_id)
        writer.write_datetime_value("readAt", self.read_at)
        writer.write_str_value("title", self.title)
        writer.write_str_value("type", self.type)
        writer.write_uuid_value("workspaceId", self.workspace_id)
        writer.write_additional_data_value(self.additional_data)
    

