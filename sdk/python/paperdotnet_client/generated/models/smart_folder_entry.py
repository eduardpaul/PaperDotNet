from __future__ import annotations
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union
from uuid import UUID

if TYPE_CHECKING:
    from .item_response import ItemResponse

@dataclass
class SmartFolderEntry(AdditionalDataHolder, Parsable):
    """
    An item in a smart folder, with where it lives.
    """
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # A list item as returned by the API. `fields` contains `title` and all field values.
    item: Optional[ItemResponse] = None
    # The listName property
    list_name: Optional[str] = None
    # The workspaceId property
    workspace_id: Optional[UUID] = None
    
    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> SmartFolderEntry:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: SmartFolderEntry
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return SmartFolderEntry()
    
    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        from .item_response import ItemResponse

        from .item_response import ItemResponse

        fields: dict[str, Callable[[Any], None]] = {
            "item": lambda n : setattr(self, 'item', n.get_object_value(ItemResponse)),
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
        writer.write_object_value("item", self.item)
        writer.write_str_value("listName", self.list_name)
        writer.write_uuid_value("workspaceId", self.workspace_id)
        writer.write_additional_data_value(self.additional_data)
    

