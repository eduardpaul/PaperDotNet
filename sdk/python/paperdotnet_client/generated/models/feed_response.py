from __future__ import annotations
import datetime
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union
from uuid import UUID

@dataclass
class FeedResponse(AdditionalDataHolder, Parsable):
    """
    A calendar feed; string? FeedResponse.Url (with the secret) is only returned when the feed is created.
    """
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # The createdAt property
    created_at: Optional[datetime.datetime] = None
    # The id property
    id: Optional[UUID] = None
    # The listId property
    list_id: Optional[UUID] = None
    # The name property
    name: Optional[str] = None
    # The url property
    url: Optional[str] = None
    # The workspaceId property
    workspace_id: Optional[UUID] = None
    
    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> FeedResponse:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: FeedResponse
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return FeedResponse()
    
    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        fields: dict[str, Callable[[Any], None]] = {
            "createdAt": lambda n : setattr(self, 'created_at', n.get_datetime_value()),
            "id": lambda n : setattr(self, 'id', n.get_uuid_value()),
            "listId": lambda n : setattr(self, 'list_id', n.get_uuid_value()),
            "name": lambda n : setattr(self, 'name', n.get_str_value()),
            "url": lambda n : setattr(self, 'url', n.get_str_value()),
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
        writer.write_datetime_value("createdAt", self.created_at)
        writer.write_uuid_value("id", self.id)
        writer.write_uuid_value("listId", self.list_id)
        writer.write_str_value("name", self.name)
        writer.write_str_value("url", self.url)
        writer.write_uuid_value("workspaceId", self.workspace_id)
        writer.write_additional_data_value(self.additional_data)
    

