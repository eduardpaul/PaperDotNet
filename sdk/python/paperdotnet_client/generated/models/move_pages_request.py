from __future__ import annotations
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union
from uuid import UUID

@dataclass
class MovePagesRequest(AdditionalDataHolder, Parsable):
    """
    Moves pages (all when omitted) into another document (DOC-06): `append` (default), `prepend`, or `replace`its pages. A source left without pages goes to the recycle bin, so moving all pages merges two documents.
    """
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # The pages property
    pages: Optional[list[int]] = None
    # The position property
    position: Optional[str] = None
    # The targetItemId property
    target_item_id: Optional[UUID] = None
    # The targetListId property
    target_list_id: Optional[UUID] = None
    # The targetWorkspaceId property
    target_workspace_id: Optional[UUID] = None
    
    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> MovePagesRequest:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: MovePagesRequest
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return MovePagesRequest()
    
    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        fields: dict[str, Callable[[Any], None]] = {
            "pages": lambda n : setattr(self, 'pages', n.get_collection_of_primitive_values(int)),
            "position": lambda n : setattr(self, 'position', n.get_str_value()),
            "targetItemId": lambda n : setattr(self, 'target_item_id', n.get_uuid_value()),
            "targetListId": lambda n : setattr(self, 'target_list_id', n.get_uuid_value()),
            "targetWorkspaceId": lambda n : setattr(self, 'target_workspace_id', n.get_uuid_value()),
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
        writer.write_collection_of_primitive_values("pages", self.pages)
        writer.write_str_value("position", self.position)
        writer.write_uuid_value("targetItemId", self.target_item_id)
        writer.write_uuid_value("targetListId", self.target_list_id)
        writer.write_uuid_value("targetWorkspaceId", self.target_workspace_id)
        writer.write_additional_data_value(self.additional_data)
    

