from __future__ import annotations
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union
from uuid import UUID

if TYPE_CHECKING:
    from .smart_folder_group_by import SmartFolderGroupBy

@dataclass
class SmartFolderDefinition(AdditionalDataHolder, Parsable):
    """
    What a smart folder shows (TAX-08). All parts are optional and combine with "and":`lists` (list names), `listTemplates` (e.g. `tasks`, `events`), `contentTypes` (names or keys),`terms` (term ids, with their child terms; `termMatch``all` or `any`), and an OData`filter` over fields (with `@me`, `@today`, …). `groupBy` adds virtual sub-folders (TAX-10).
    """
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # The includeFolders property
    include_folders: Optional[bool] = False
    # The contentTypes property
    content_types: Optional[list[str]] = None
    # The filter property
    filter: Optional[str] = None
    # The groupBy property
    group_by: Optional[list[SmartFolderGroupBy]] = None
    # The listTemplates property
    list_templates: Optional[list[str]] = None
    # The lists property
    lists: Optional[list[str]] = None
    # The termMatch property
    term_match: Optional[str] = None
    # The terms property
    terms: Optional[list[UUID]] = None
    
    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> SmartFolderDefinition:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: SmartFolderDefinition
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return SmartFolderDefinition()
    
    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        from .smart_folder_group_by import SmartFolderGroupBy

        from .smart_folder_group_by import SmartFolderGroupBy

        fields: dict[str, Callable[[Any], None]] = {
            "contentTypes": lambda n : setattr(self, 'content_types', n.get_collection_of_primitive_values(str)),
            "filter": lambda n : setattr(self, 'filter', n.get_str_value()),
            "groupBy": lambda n : setattr(self, 'group_by', n.get_collection_of_object_values(SmartFolderGroupBy)),
            "includeFolders": lambda n : setattr(self, 'include_folders', n.get_bool_value()),
            "listTemplates": lambda n : setattr(self, 'list_templates', n.get_collection_of_primitive_values(str)),
            "lists": lambda n : setattr(self, 'lists', n.get_collection_of_primitive_values(str)),
            "termMatch": lambda n : setattr(self, 'term_match', n.get_str_value()),
            "terms": lambda n : setattr(self, 'terms', n.get_collection_of_primitive_values(UUID)),
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
        writer.write_collection_of_primitive_values("contentTypes", self.content_types)
        writer.write_str_value("filter", self.filter)
        writer.write_collection_of_object_values("groupBy", self.group_by)
        writer.write_bool_value("includeFolders", self.include_folders)
        writer.write_collection_of_primitive_values("listTemplates", self.list_templates)
        writer.write_collection_of_primitive_values("lists", self.lists)
        writer.write_str_value("termMatch", self.term_match)
        writer.write_collection_of_primitive_values("terms", self.terms)
        writer.write_additional_data_value(self.additional_data)
    

