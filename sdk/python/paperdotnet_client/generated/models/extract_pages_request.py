from __future__ import annotations
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union
from uuid import UUID

@dataclass
class ExtractPagesRequest(AdditionalDataHolder, Parsable):
    """
    Extracts pages into new documents (DOC-06): one document, or one per page with `separate`. The target library andfolder default to the source's library and folder; `remove` also deletes the pages from the source.
    """
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # The remove property
    remove: Optional[bool] = False
    # The separate property
    separate: Optional[bool] = False
    # The folderId property
    folder_id: Optional[UUID] = None
    # The listId property
    list_id: Optional[UUID] = None
    # The pages property
    pages: Optional[list[int]] = None
    # The title property
    title: Optional[str] = None
    # The workspaceId property
    workspace_id: Optional[UUID] = None
    
    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> ExtractPagesRequest:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: ExtractPagesRequest
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return ExtractPagesRequest()
    
    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        fields: dict[str, Callable[[Any], None]] = {
            "folderId": lambda n : setattr(self, 'folder_id', n.get_uuid_value()),
            "listId": lambda n : setattr(self, 'list_id', n.get_uuid_value()),
            "pages": lambda n : setattr(self, 'pages', n.get_collection_of_primitive_values(int)),
            "remove": lambda n : setattr(self, 'remove', n.get_bool_value()),
            "separate": lambda n : setattr(self, 'separate', n.get_bool_value()),
            "title": lambda n : setattr(self, 'title', n.get_str_value()),
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
        writer.write_uuid_value("folderId", self.folder_id)
        writer.write_uuid_value("listId", self.list_id)
        writer.write_collection_of_primitive_values("pages", self.pages)
        writer.write_bool_value("remove", self.remove)
        writer.write_bool_value("separate", self.separate)
        writer.write_str_value("title", self.title)
        writer.write_uuid_value("workspaceId", self.workspace_id)
        writer.write_additional_data_value(self.additional_data)
    

