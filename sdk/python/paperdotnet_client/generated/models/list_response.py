from __future__ import annotations
import datetime
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union
from uuid import UUID

if TYPE_CHECKING:
    from .content_type_response import ContentTypeResponse
    from .field_definition_dto import FieldDefinitionDto
    from .list_kind import ListKind
    from .list_versioning import ListVersioning

@dataclass
class ListResponse(AdditionalDataHolder, Parsable):
    """
    A list with its content types and effective columns.
    """
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # The allowFolders property
    allow_folders: Optional[bool] = None
    # The columns property
    columns: Optional[list[FieldDefinitionDto]] = None
    # The contentTypes property
    content_types: Optional[list[ContentTypeResponse]] = None
    # The createdAt property
    created_at: Optional[datetime.datetime] = None
    # The description property
    description: Optional[str] = None
    # The id property
    id: Optional[UUID] = None
    # The kind property
    kind: Optional[ListKind] = None
    # The maxVersions property
    max_versions: Optional[int] = None
    # The name property
    name: Optional[str] = None
    # The templateKey property
    template_key: Optional[str] = None
    # The updatedAt property
    updated_at: Optional[datetime.datetime] = None
    # Version history of a list (LST-11). Minor versions (drafts) come with documents.
    versioning: Optional[ListVersioning] = None
    # The workspaceId property
    workspace_id: Optional[UUID] = None
    
    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> ListResponse:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: ListResponse
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return ListResponse()
    
    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        from .content_type_response import ContentTypeResponse
        from .field_definition_dto import FieldDefinitionDto
        from .list_kind import ListKind
        from .list_versioning import ListVersioning

        from .content_type_response import ContentTypeResponse
        from .field_definition_dto import FieldDefinitionDto
        from .list_kind import ListKind
        from .list_versioning import ListVersioning

        fields: dict[str, Callable[[Any], None]] = {
            "allowFolders": lambda n : setattr(self, 'allow_folders', n.get_bool_value()),
            "columns": lambda n : setattr(self, 'columns', n.get_collection_of_object_values(FieldDefinitionDto)),
            "contentTypes": lambda n : setattr(self, 'content_types', n.get_collection_of_object_values(ContentTypeResponse)),
            "createdAt": lambda n : setattr(self, 'created_at', n.get_datetime_value()),
            "description": lambda n : setattr(self, 'description', n.get_str_value()),
            "id": lambda n : setattr(self, 'id', n.get_uuid_value()),
            "kind": lambda n : setattr(self, 'kind', n.get_enum_value(ListKind)),
            "maxVersions": lambda n : setattr(self, 'max_versions', n.get_int_value()),
            "name": lambda n : setattr(self, 'name', n.get_str_value()),
            "templateKey": lambda n : setattr(self, 'template_key', n.get_str_value()),
            "updatedAt": lambda n : setattr(self, 'updated_at', n.get_datetime_value()),
            "versioning": lambda n : setattr(self, 'versioning', n.get_enum_value(ListVersioning)),
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
        writer.write_bool_value("allowFolders", self.allow_folders)
        writer.write_collection_of_object_values("columns", self.columns)
        writer.write_collection_of_object_values("contentTypes", self.content_types)
        writer.write_datetime_value("createdAt", self.created_at)
        writer.write_str_value("description", self.description)
        writer.write_uuid_value("id", self.id)
        writer.write_enum_value("kind", self.kind)
        writer.write_int_value("maxVersions", self.max_versions)
        writer.write_str_value("name", self.name)
        writer.write_str_value("templateKey", self.template_key)
        writer.write_datetime_value("updatedAt", self.updated_at)
        writer.write_enum_value("versioning", self.versioning)
        writer.write_uuid_value("workspaceId", self.workspace_id)
        writer.write_additional_data_value(self.additional_data)
    

