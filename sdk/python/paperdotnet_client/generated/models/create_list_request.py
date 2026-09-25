from __future__ import annotations
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union
from uuid import UUID

if TYPE_CHECKING:
    from .list_kind import ListKind
    from .list_versioning import ListVersioning

@dataclass
class CreateListRequest(AdditionalDataHolder, Parsable):
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # The allowFolders property
    allow_folders: Optional[bool] = True
    # The maxVersions property
    max_versions: Optional[int] = 50
    # The contentTypeIds property
    content_type_ids: Optional[list[UUID]] = None
    # The description property
    description: Optional[str] = None
    # The kind property
    kind: Optional[ListKind] = None
    # The name property
    name: Optional[str] = None
    # The templateKey property
    template_key: Optional[str] = None
    # The versioning property
    versioning: Optional[ListVersioning] = None
    
    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> CreateListRequest:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: CreateListRequest
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return CreateListRequest()
    
    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        from .list_kind import ListKind
        from .list_versioning import ListVersioning

        from .list_kind import ListKind
        from .list_versioning import ListVersioning

        fields: dict[str, Callable[[Any], None]] = {
            "allowFolders": lambda n : setattr(self, 'allow_folders', n.get_bool_value()),
            "contentTypeIds": lambda n : setattr(self, 'content_type_ids', n.get_collection_of_primitive_values(UUID)),
            "description": lambda n : setattr(self, 'description', n.get_str_value()),
            "kind": lambda n : setattr(self, 'kind', n.get_enum_value(ListKind)),
            "maxVersions": lambda n : setattr(self, 'max_versions', n.get_int_value()),
            "name": lambda n : setattr(self, 'name', n.get_str_value()),
            "templateKey": lambda n : setattr(self, 'template_key', n.get_str_value()),
            "versioning": lambda n : setattr(self, 'versioning', n.get_enum_value(ListVersioning)),
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
        writer.write_collection_of_primitive_values("contentTypeIds", self.content_type_ids)
        writer.write_str_value("description", self.description)
        writer.write_enum_value("kind", self.kind)
        writer.write_int_value("maxVersions", self.max_versions)
        writer.write_str_value("name", self.name)
        writer.write_str_value("templateKey", self.template_key)
        writer.write_enum_value("versioning", self.versioning)
        writer.write_additional_data_value(self.additional_data)
    

