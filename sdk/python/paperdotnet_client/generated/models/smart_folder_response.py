from __future__ import annotations
import datetime
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union
from uuid import UUID

if TYPE_CHECKING:
    from .smart_folder_definition import SmartFolderDefinition

@dataclass
class SmartFolderResponse(AdditionalDataHolder, Parsable):
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # The createdAt property
    created_at: Optional[datetime.datetime] = None
    # The createdBy property
    created_by: Optional[UUID] = None
    # What a smart folder shows (TAX-08). All parts are optional and combine with "and":`lists` (list names), `listTemplates` (e.g. `tasks`, `events`), `contentTypes` (names or keys),`terms` (term ids, with their child terms; `termMatch``all` or `any`), and an OData`filter` over fields (with `@me`, `@today`, …). `groupBy` adds virtual sub-folders (TAX-10).
    definition: Optional[SmartFolderDefinition] = None
    # The description property
    description: Optional[str] = None
    # The id property
    id: Optional[UUID] = None
    # The name property
    name: Optional[str] = None
    # The personal property
    personal: Optional[bool] = None
    # The updatedAt property
    updated_at: Optional[datetime.datetime] = None
    # The workspaceId property
    workspace_id: Optional[UUID] = None
    
    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> SmartFolderResponse:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: SmartFolderResponse
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return SmartFolderResponse()
    
    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        from .smart_folder_definition import SmartFolderDefinition

        from .smart_folder_definition import SmartFolderDefinition

        fields: dict[str, Callable[[Any], None]] = {
            "createdAt": lambda n : setattr(self, 'created_at', n.get_datetime_value()),
            "createdBy": lambda n : setattr(self, 'created_by', n.get_uuid_value()),
            "definition": lambda n : setattr(self, 'definition', n.get_object_value(SmartFolderDefinition)),
            "description": lambda n : setattr(self, 'description', n.get_str_value()),
            "id": lambda n : setattr(self, 'id', n.get_uuid_value()),
            "name": lambda n : setattr(self, 'name', n.get_str_value()),
            "personal": lambda n : setattr(self, 'personal', n.get_bool_value()),
            "updatedAt": lambda n : setattr(self, 'updated_at', n.get_datetime_value()),
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
        writer.write_uuid_value("createdBy", self.created_by)
        writer.write_object_value("definition", self.definition)
        writer.write_str_value("description", self.description)
        writer.write_uuid_value("id", self.id)
        writer.write_str_value("name", self.name)
        writer.write_bool_value("personal", self.personal)
        writer.write_datetime_value("updatedAt", self.updated_at)
        writer.write_uuid_value("workspaceId", self.workspace_id)
        writer.write_additional_data_value(self.additional_data)
    

