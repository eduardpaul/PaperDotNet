from __future__ import annotations
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union
from uuid import UUID

@dataclass
class GroupInboxResponse(AdditionalDataHolder, Parsable):
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # The groupId property
    group_id: Optional[UUID] = None
    # The groupName property
    group_name: Optional[str] = None
    # The listId property
    list_id: Optional[UUID] = None
    # The listName property
    list_name: Optional[str] = None
    # The workspaceId property
    workspace_id: Optional[UUID] = None
    
    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> GroupInboxResponse:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: GroupInboxResponse
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return GroupInboxResponse()
    
    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        fields: dict[str, Callable[[Any], None]] = {
            "groupId": lambda n : setattr(self, 'group_id', n.get_uuid_value()),
            "groupName": lambda n : setattr(self, 'group_name', n.get_str_value()),
            "listId": lambda n : setattr(self, 'list_id', n.get_uuid_value()),
            "listName": lambda n : setattr(self, 'list_name', n.get_str_value()),
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
        writer.write_uuid_value("groupId", self.group_id)
        writer.write_str_value("groupName", self.group_name)
        writer.write_uuid_value("listId", self.list_id)
        writer.write_str_value("listName", self.list_name)
        writer.write_uuid_value("workspaceId", self.workspace_id)
        writer.write_additional_data_value(self.additional_data)
    

