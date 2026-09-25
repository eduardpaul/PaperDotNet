from __future__ import annotations
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union
from uuid import UUID

if TYPE_CHECKING:
    from .json_object import JsonObject

@dataclass
class SmartFolderDropRequest(AdditionalDataHolder, Parsable):
    """
    Drop to classify (TAX-09): an existing item (`itemId`) gets the folder's terms and the values of its`eq` conditions (and of the sub-folder `path`); or a new item is created with them (`fields`).
    """
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # The fields property
    fields: Optional[JsonObject] = None
    # The itemId property
    item_id: Optional[UUID] = None
    # The listId property
    list_id: Optional[UUID] = None
    # The path property
    path: Optional[list[str]] = None
    # The workspaceId property
    workspace_id: Optional[UUID] = None
    
    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> SmartFolderDropRequest:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: SmartFolderDropRequest
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return SmartFolderDropRequest()
    
    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        from .json_object import JsonObject

        from .json_object import JsonObject

        fields: dict[str, Callable[[Any], None]] = {
            "fields": lambda n : setattr(self, 'fields', n.get_object_value(JsonObject)),
            "itemId": lambda n : setattr(self, 'item_id', n.get_uuid_value()),
            "listId": lambda n : setattr(self, 'list_id', n.get_uuid_value()),
            "path": lambda n : setattr(self, 'path', n.get_collection_of_primitive_values(str)),
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
        writer.write_object_value("fields", self.fields)
        writer.write_uuid_value("itemId", self.item_id)
        writer.write_uuid_value("listId", self.list_id)
        writer.write_collection_of_primitive_values("path", self.path)
        writer.write_uuid_value("workspaceId", self.workspace_id)
        writer.write_additional_data_value(self.additional_data)
    

