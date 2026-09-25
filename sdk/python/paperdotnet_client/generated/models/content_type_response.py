from __future__ import annotations
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union
from uuid import UUID

if TYPE_CHECKING:
    from .field_definition_dto import FieldDefinitionDto

@dataclass
class ContentTypeResponse(AdditionalDataHolder, Parsable):
    """
    A content type. `key` is set when it comes from a template; `extensionId` when an extension manages it (read-only).
    """
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # The description property
    description: Optional[str] = None
    # The extensionId property
    extension_id: Optional[str] = None
    # The fields property
    fields: Optional[list[FieldDefinitionDto]] = None
    # The id property
    id: Optional[UUID] = None
    # The isBuiltIn property
    is_built_in: Optional[bool] = None
    # The key property
    key: Optional[str] = None
    # The name property
    name: Optional[str] = None
    # The ETag for `If-Match` on changes (the same as the `ETag` header).
    odata_etag: Optional[str] = None
    
    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> ContentTypeResponse:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: ContentTypeResponse
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return ContentTypeResponse()
    
    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        from .field_definition_dto import FieldDefinitionDto

        from .field_definition_dto import FieldDefinitionDto

        fields: dict[str, Callable[[Any], None]] = {
            "description": lambda n : setattr(self, 'description', n.get_str_value()),
            "extensionId": lambda n : setattr(self, 'extension_id', n.get_str_value()),
            "fields": lambda n : setattr(self, 'fields', n.get_collection_of_object_values(FieldDefinitionDto)),
            "id": lambda n : setattr(self, 'id', n.get_uuid_value()),
            "isBuiltIn": lambda n : setattr(self, 'is_built_in', n.get_bool_value()),
            "key": lambda n : setattr(self, 'key', n.get_str_value()),
            "name": lambda n : setattr(self, 'name', n.get_str_value()),
            "@odata.etag": lambda n : setattr(self, 'odata_etag', n.get_str_value()),
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
        writer.write_str_value("description", self.description)
        writer.write_str_value("extensionId", self.extension_id)
        writer.write_collection_of_object_values("fields", self.fields)
        writer.write_uuid_value("id", self.id)
        writer.write_bool_value("isBuiltIn", self.is_built_in)
        writer.write_str_value("key", self.key)
        writer.write_str_value("name", self.name)
        writer.write_str_value("@odata.etag", self.odata_etag)
        writer.write_additional_data_value(self.additional_data)
    

