from __future__ import annotations
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union
from uuid import UUID

if TYPE_CHECKING:
    from .duplicate_response import DuplicateResponse
    from .file_version_response import FileVersionResponse
    from .json_object import JsonObject

@dataclass
class DocumentResponse(AdditionalDataHolder, Parsable):
    """
    An uploaded document: the library item and its current file.
    """
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # The duplicates property
    duplicates: Optional[list[DuplicateResponse]] = None
    # The fields property
    fields: Optional[JsonObject] = None
    # The file property
    file: Optional[FileVersionResponse] = None
    # The itemId property
    item_id: Optional[UUID] = None
    # The listId property
    list_id: Optional[UUID] = None
    # The workspaceId property
    workspace_id: Optional[UUID] = None
    
    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> DocumentResponse:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: DocumentResponse
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return DocumentResponse()
    
    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        from .duplicate_response import DuplicateResponse
        from .file_version_response import FileVersionResponse
        from .json_object import JsonObject

        from .duplicate_response import DuplicateResponse
        from .file_version_response import FileVersionResponse
        from .json_object import JsonObject

        fields: dict[str, Callable[[Any], None]] = {
            "duplicates": lambda n : setattr(self, 'duplicates', n.get_collection_of_object_values(DuplicateResponse)),
            "fields": lambda n : setattr(self, 'fields', n.get_object_value(JsonObject)),
            "file": lambda n : setattr(self, 'file', n.get_object_value(FileVersionResponse)),
            "itemId": lambda n : setattr(self, 'item_id', n.get_uuid_value()),
            "listId": lambda n : setattr(self, 'list_id', n.get_uuid_value()),
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
        writer.write_collection_of_object_values("duplicates", self.duplicates)
        writer.write_object_value("fields", self.fields)
        writer.write_object_value("file", self.file)
        writer.write_uuid_value("itemId", self.item_id)
        writer.write_uuid_value("listId", self.list_id)
        writer.write_uuid_value("workspaceId", self.workspace_id)
        writer.write_additional_data_value(self.additional_data)
    

