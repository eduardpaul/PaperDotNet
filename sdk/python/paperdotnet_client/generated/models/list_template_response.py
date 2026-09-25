from __future__ import annotations
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union

if TYPE_CHECKING:
    from .list_template_content_type import ListTemplateContentType

@dataclass
class ListTemplateResponse(AdditionalDataHolder, Parsable):
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # The contentTypes property
    content_types: Optional[list[ListTemplateContentType]] = None
    # The description property
    description: Optional[str] = None
    # The extensionId property
    extension_id: Optional[str] = None
    # The isLibrary property
    is_library: Optional[bool] = None
    # The key property
    key: Optional[str] = None
    # The name property
    name: Optional[str] = None
    # The versioning property
    versioning: Optional[bool] = None
    # The views property
    views: Optional[list[str]] = None
    
    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> ListTemplateResponse:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: ListTemplateResponse
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return ListTemplateResponse()
    
    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        from .list_template_content_type import ListTemplateContentType

        from .list_template_content_type import ListTemplateContentType

        fields: dict[str, Callable[[Any], None]] = {
            "contentTypes": lambda n : setattr(self, 'content_types', n.get_collection_of_object_values(ListTemplateContentType)),
            "description": lambda n : setattr(self, 'description', n.get_str_value()),
            "extensionId": lambda n : setattr(self, 'extension_id', n.get_str_value()),
            "isLibrary": lambda n : setattr(self, 'is_library', n.get_bool_value()),
            "key": lambda n : setattr(self, 'key', n.get_str_value()),
            "name": lambda n : setattr(self, 'name', n.get_str_value()),
            "versioning": lambda n : setattr(self, 'versioning', n.get_bool_value()),
            "views": lambda n : setattr(self, 'views', n.get_collection_of_primitive_values(str)),
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
        writer.write_collection_of_object_values("contentTypes", self.content_types)
        writer.write_str_value("description", self.description)
        writer.write_str_value("extensionId", self.extension_id)
        writer.write_bool_value("isLibrary", self.is_library)
        writer.write_str_value("key", self.key)
        writer.write_str_value("name", self.name)
        writer.write_bool_value("versioning", self.versioning)
        writer.write_collection_of_primitive_values("views", self.views)
        writer.write_additional_data_value(self.additional_data)
    

